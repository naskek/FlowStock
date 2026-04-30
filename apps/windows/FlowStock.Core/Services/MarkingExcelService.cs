using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

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
            .ToList();
    }

    public MarkingExcelExportResult Export(IReadOnlyCollection<long> orderIds, DateTime generatedAt)
    {
        var normalizedOrderIds = orderIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (normalizedOrderIds.Length == 0)
        {
            return MarkingExcelExportResult.Empty("Выберите хотя бы один заказ.");
        }

        var candidates = _data.GetMarkingOrderLineCandidates(normalizedOrderIds)
            .Select(NormalizeCandidate)
            .Where(line => line.ItemTypeEnableMarking
                           && !string.IsNullOrWhiteSpace(line.Gtin)
                           && line.QtyForMarking > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return MarkingExcelExportResult.Empty("Нет строк для формирования файла ЧЗ.");
        }

        var rows = candidates
            .GroupBy(line => (Gtin: line.Gtin!.ToUpperInvariant(), ItemName: line.ItemName.ToUpperInvariant()))
            .Select(group => new MarkingExportRow
            {
                ItemName = group.First().ItemName,
                Gtin = group.First().Gtin!,
                Qty = group.Sum(line => line.QtyForMarking)
            })
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Gtin, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orderIdsWithRows = candidates
            .Select(line => line.OrderId)
            .Distinct()
            .ToArray();
        var bytes = BuildWorkbook(rows);
        _data.MarkOrdersPrinted(orderIdsWithRows, generatedAt);

        return new MarkingExcelExportResult(
            IsSuccess: true,
            Error: null,
            FileBytes: bytes,
            Rows: rows,
            MarkedOrderIds: orderIdsWithRows);
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
        var status = row.MarkingStatus == MarkingStatus.NotRequired && row.MarkingLineCount > 0
            ? MarkingStatus.Required
            : row.MarkingStatus;
        return new MarkingOrderQueueRow
        {
            OrderId = row.OrderId,
            OrderRef = row.OrderRef,
            PartnerName = row.PartnerName,
            PartnerCode = row.PartnerCode,
            OrderStatus = row.OrderStatus,
            DueDate = row.DueDate,
            MarkingStatus = status,
            MarkingLineCount = row.MarkingLineCount,
            MarkingCodeCount = row.MarkingCodeCount,
            LastGeneratedAt = row.LastGeneratedAt
        };
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
        AppendTextRow(builder, 1, "Наименование", "GTIN", "Кол-во");
        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2;
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

    private static void AppendTextRow(StringBuilder builder, int rowNumber, string first, string second, string third)
    {
        builder.Append("    <row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("\">");
        AppendTextCell(builder, "A", rowNumber, first);
        AppendTextCell(builder, "B", rowNumber, second);
        AppendTextCell(builder, "C", rowNumber, third);
        builder.AppendLine("    </row>");
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
}

public sealed record MarkingExcelExportResult(
    bool IsSuccess,
    string? Error,
    byte[]? FileBytes,
    IReadOnlyList<MarkingExportRow> Rows,
    IReadOnlyList<long> MarkedOrderIds)
{
    public static MarkingExcelExportResult Empty(string error)
    {
        return new MarkingExcelExportResult(false, error, null, Array.Empty<MarkingExportRow>(), Array.Empty<long>());
    }
}
