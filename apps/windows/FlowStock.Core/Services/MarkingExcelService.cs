using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public sealed class MarkingExcelService
{
    private readonly IDataStore _data;

    public MarkingExcelService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<MarkingOrderQueueRow> GetOrderQueue(bool includeCompleted)
    {
        return _data.GetMarkingOrderQueue(includeCompleted)
            .Select(NormalizeQueueRow)
            .Where(row => includeCompleted || !IsTaskCodeCovered(row))
            .ToList();
    }

    public MarkingExcelExportResult Export(IReadOnlyCollection<long> orderIds, DateTime generatedAt)
    {
        return Export(Array.Empty<Guid>(), orderIds, generatedAt);
    }

    public MarkingExcelExportResult Export(
        IReadOnlyCollection<Guid> markingOrderIds,
        IReadOnlyCollection<long> orderIds,
        DateTime generatedAt)
    {
        var normalizedOrderIds = orderIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var normalizedMarkingOrderIds = markingOrderIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (normalizedOrderIds.Length == 0 && normalizedMarkingOrderIds.Length == 0)
        {
            return MarkingExcelExportResult.Empty("Выберите хотя бы одну задачу маркировки.");
        }

        var orderRows = normalizedOrderIds.Length == 0
            ? new List<MarkingOrderLineCandidate>()
            : _data.GetMarkingOrderLineCandidates(normalizedOrderIds)
                .Select(NormalizeCandidate)
                .Where(line => line.ItemTypeEnableMarking
                               && !string.IsNullOrWhiteSpace(line.Gtin)
                               && line.QtyForMarking > 0)
                .ToList();
        var taskRows = BuildTaskRows(normalizedMarkingOrderIds);
        var exportRows = orderRows
            .Select(line => new MarkingExportRow
            {
                ItemName = line.ItemName,
                Gtin = line.Gtin!,
                Qty = line.QtyForMarking
            })
            .Concat(taskRows.Select(line => new MarkingExportRow
            {
                ItemName = line.ItemName,
                Gtin = line.Gtin,
                Qty = line.Qty
            }))
            .ToList();
        if (exportRows.Count == 0)
        {
            return MarkingExcelExportResult.Empty("Нет строк для формирования файла ЧЗ.");
        }

        var rows = exportRows
            .GroupBy(line => (Gtin: line.Gtin.ToUpperInvariant(), ItemName: line.ItemName.ToUpperInvariant()))
            .Select(group => new MarkingExportRow
            {
                ItemName = group.First().ItemName,
                Gtin = group.First().Gtin,
                Qty = group.Sum(line => line.Qty)
            })
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Gtin, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var markingOrderIdsWithRows = taskRows
            .Select(line => line.MarkingOrderId)
            .Distinct()
            .ToArray();
        var orderIdsWithRows = orderRows
            .Select(line => line.OrderId)
            .Concat(taskRows.Where(line => line.OrderId.HasValue).Select(line => line.OrderId!.Value))
            .Distinct()
            .ToArray();
        var bytes = BuildWorkbook(rows);
        if (orderIdsWithRows.Length > 0 || markingOrderIdsWithRows.Length > 0)
        {
            _data.ExecuteInTransaction(store =>
            {
                if (orderIdsWithRows.Length > 0)
                {
                    store.MarkOrdersPrinted(orderIdsWithRows, generatedAt);
                }

                if (markingOrderIdsWithRows.Length > 0)
                {
                    store.MarkMarkingOrdersPrinted(markingOrderIdsWithRows, generatedAt);
                    EnsureTemporaryMarkingCodes(store, taskRows, generatedAt);
                }
            });
        }

        return new MarkingExcelExportResult(
            IsSuccess: true,
            Error: null,
            FileBytes: bytes,
            Rows: rows,
            MarkedOrderIds: orderIdsWithRows,
            MarkedMarkingOrderIds: markingOrderIdsWithRows);
    }

    private IReadOnlyList<MarkingTaskExportRow> BuildTaskRows(IReadOnlyCollection<Guid> markingOrderIds)
    {
        if (markingOrderIds.Count == 0)
        {
            return Array.Empty<MarkingTaskExportRow>();
        }

        return _data.GetMarkingOrdersByIds(markingOrderIds)
            .Where(order => !IsTerminalFailed(order.Status)
                            && order.RequestedQuantity > 0
                            && order.ItemId.HasValue)
            .Select(order => (Order: order, Item: _data.FindItemById(order.ItemId!.Value)))
            .Where(pair => pair.Item?.ItemTypeEnableMarking == true
                           && !string.IsNullOrWhiteSpace(pair.Order.Gtin ?? pair.Item.Gtin))
            .Select(pair =>
            {
                var gtin = string.IsNullOrWhiteSpace(pair.Order.Gtin)
                    ? pair.Item!.Gtin!.Trim()
                    : pair.Order.Gtin!.Trim();
                return new MarkingTaskExportRow(
                    MarkingOrderId: pair.Order.Id,
                    OrderId: pair.Order.OrderId,
                    ItemName: pair.Item!.Name,
                    Gtin: gtin,
                    Qty: pair.Order.RequestedQuantity);
            })
            .ToList();
    }

    private static void EnsureTemporaryMarkingCodes(
        IDataStore store,
        IReadOnlyList<MarkingTaskExportRow> taskRows,
        DateTime generatedAt)
    {
        foreach (var row in taskRows)
        {
            var requested = (int)Math.Ceiling(Math.Max(0, row.Qty));
            if (requested <= 0)
            {
                continue;
            }

            var existing = store.CountMarkingCodesByMarkingOrder(row.MarkingOrderId);
            var missing = requested - existing;
            if (missing <= 0)
            {
                continue;
            }

            var importId = Guid.NewGuid();
            store.AddMarkingCodeImport(new MarkingCodeImport
            {
                Id = importId,
                OriginalFilename = $"TEMP-CHZ-{row.MarkingOrderId:D}.xlsx",
                StoragePath = "<temporary-chz-export>",
                FileHash = ComputeCodeHash($"TEMP-CHZ-IMPORT-{row.MarkingOrderId:D}-{generatedAt:O}-{existing}-{missing}"),
                SourceType = "temporary-chz-export",
                DetectedGtin = row.Gtin,
                DetectedQuantity = missing,
                MatchedMarkingOrderId = row.MarkingOrderId,
                MatchConfidence = 1m,
                Status = MarkingCodeImportStatus.Bound,
                ImportedRows = missing,
                ValidCodeRows = missing,
                DuplicateCodeRows = 0,
                CreatedAt = generatedAt,
                ProcessedAt = generatedAt
            });

            var codes = Enumerable.Range(existing + 1, missing)
                .Select(index =>
                {
                    var code = $"TEMP-CHZ-{row.MarkingOrderId:D}-{index:000000}";
                    return new MarkingCode
                    {
                        Id = Guid.NewGuid(),
                        Code = code,
                        CodeHash = ComputeCodeHash(code),
                        Gtin = row.Gtin,
                        MarkingOrderId = row.MarkingOrderId,
                        ImportId = importId,
                        Status = MarkingCodeStatus.Reserved,
                        Origin = MarkingCodeOrigin.LegacySynthetic,
                        SourceRowNumber = index,
                        CreatedAt = generatedAt,
                        UpdatedAt = generatedAt
                    };
                })
                .ToArray();
            store.AddMarkingCodes(codes);
        }
    }

    private static bool IsTerminalFailed(string? status)
    {
        return string.Equals(status, MarkingOrderStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, MarkingOrderStatus.Failed, StringComparison.OrdinalIgnoreCase);
    }

    private static MarkingOrderLineCandidate NormalizeCandidate(MarkingOrderLineCandidate line)
    {
        var qtyForMarking = Math.Max(0, line.QtyOrdered - line.ShippedQty - line.ReservedQty);
        return new MarkingOrderLineCandidate
        {
            OrderId = line.OrderId,
            OrderLineId = line.OrderLineId,
            ItemName = line.ItemName,
            Gtin = string.IsNullOrWhiteSpace(line.Gtin) ? null : line.Gtin.Trim(),
            ItemTypeEnableMarking = line.ItemTypeEnableMarking,
            QtyOrdered = line.QtyOrdered,
            ShippedQty = line.ShippedQty,
            ReservedQty = line.ReservedQty,
            QtyForMarking = qtyForMarking
        };
    }

    private static MarkingOrderQueueRow NormalizeQueueRow(MarkingOrderQueueRow row)
    {
        var isTaskCodeCovered = IsTaskCodeCovered(row);
        var status = isTaskCodeCovered
            ? MarkingStatus.Printed
            : MarkingStatusResolver.Resolve(
                row.MarkingStatus,
                row.MarkingLineCount > 0,
                row.OrderStatus);
        return new MarkingOrderQueueRow
        {
            MarkingOrderId = row.MarkingOrderId,
            OrderId = row.OrderId,
            OrderRef = row.OrderRef,
            PartnerName = row.PartnerName,
            PartnerCode = row.PartnerCode,
            SourceType = row.SourceType,
            SourceOrderId = row.SourceOrderId,
            ItemId = row.ItemId,
            ItemName = row.ItemName,
            Gtin = row.Gtin,
            RequestedQuantity = row.RequestedQuantity,
            TaskStatus = row.TaskStatus,
            CodesTotal = row.CodesTotal,
            CodesFree = row.CodesFree,
            CodesBound = row.CodesBound,
            DisplaySource = row.DisplaySource,
            EffectiveStatus = isTaskCodeCovered ? MarkingOrderStatus.Completed : row.EffectiveStatus,
            DisplayStatus = isTaskCodeCovered ? "Выполнена" : row.DisplayStatus,
            OrderStatus = row.OrderStatus,
            DueDate = row.DueDate,
            MarkingStatus = status,
            MarkingLineCount = row.MarkingLineCount,
            MarkingCodeCount = row.MarkingCodeCount,
            LastGeneratedAt = row.LastGeneratedAt
        };
    }

    private static bool IsTaskCodeCovered(MarkingOrderQueueRow row)
    {
        return row.MarkingOrderId.HasValue
               && row.RequestedQuantity > 0
               && row.CodesTotal >= row.RequestedQuantity;
    }

    private static byte[] BuildWorkbook(IReadOnlyList<MarkingExportRow> rows)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""");
            AddEntry(archive, "_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""");
            AddEntry(archive, "xl/_rels/workbook.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""");
            AddEntry(archive, "xl/workbook.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets>
    <sheet name="ЧЗ" sheetId="1" r:id="rId1"/>
  </sheets>
</workbook>
""");
            AddEntry(archive, "xl/styles.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
  <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
</styleSheet>
""");
            AddEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheet(rows));
        }

        return stream.ToArray();
    }

    private static string BuildWorksheet(IReadOnlyList<MarkingExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("""  <sheetData>""");
        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 1;
            var row = rows[i];
            builder.Append("    <row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("\">");
            AppendTextCell(builder, "A", rowNumber, row.ItemName);
            AppendTextCell(builder, "B", rowNumber, row.Gtin);
            AppendNumberCell(builder, "C", rowNumber, row.Qty);
            builder.AppendLine("    </row>");
        }

        builder.AppendLine("""  </sheetData>""");
        builder.AppendLine("""</worksheet>""");
        return builder.ToString();
    }

    private static void AppendTextCell(StringBuilder builder, string column, int rowNumber, string value)
    {
        builder
            .Append("      <c r=\"").Append(column).Append(rowNumber.ToString(CultureInfo.InvariantCulture))
            .AppendLine("\" t=\"inlineStr\">")
            .Append("        <is><t>").Append(SecurityElement.Escape(value) ?? string.Empty).AppendLine("</t></is>")
            .AppendLine("      </c>");
    }

    private static void AppendNumberCell(StringBuilder builder, string column, int rowNumber, double value)
    {
        builder
            .Append("      <c r=\"").Append(column).Append(rowNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("\">")
            .Append("        <v>").Append(value.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine("</v>")
            .AppendLine("      </c>");
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ComputeCodeHash(string value)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}

internal sealed record MarkingTaskExportRow(
    Guid MarkingOrderId,
    long? OrderId,
    string ItemName,
    string Gtin,
    double Qty);

public sealed record MarkingExcelExportResult(
    bool IsSuccess,
    string? Error,
    byte[]? FileBytes,
    IReadOnlyList<MarkingExportRow> Rows,
    IReadOnlyList<long> MarkedOrderIds,
    IReadOnlyList<Guid> MarkedMarkingOrderIds)
{
    public static MarkingExcelExportResult Empty(string error)
    {
        return new MarkingExcelExportResult(false, error, null, Array.Empty<MarkingExportRow>(), Array.Empty<long>(), Array.Empty<Guid>());
    }
}
