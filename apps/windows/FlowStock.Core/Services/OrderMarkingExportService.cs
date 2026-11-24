using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowStock.Core.Services;

public sealed class OrderMarkingExportService
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _data;

    public OrderMarkingExportService(IDataStore data)
    {
        _data = data;
    }

    public OrderMarkingExportPreviewResult Preview(long orderId) =>
        _data is IReadOnlySnapshotStore snapshots
            ? snapshots.ReadSnapshot(store => new OrderMarkingExportService(store).PreviewSnapshot(orderId))
            : PreviewSnapshot(orderId);

    private OrderMarkingExportPreviewResult PreviewSnapshot(long orderId)
    {
        var order = _data.GetOrder(orderId);
        if (order == null)
        {
            return OrderMarkingExportPreviewResult.Failure("Заказ не найден.");
        }

        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            return OrderMarkingExportPreviewResult.Failure("Нельзя формировать Excel ЧЗ для завершённого или отменённого заказа.");
        }

        var huCodesByLine = order.Type == OrderType.Customer
            ? BuildProductionHuCodesByOrderLine(order.Id)
            : new Dictionary<long, IReadOnlyList<string>>();
        var lines = BuildLineSummaries(order)
            .Select(summary =>
            {
                huCodesByLine.TryGetValue(summary.OrderLineId, out var huCodes);
                huCodes ??= Array.Empty<string>();
                var previewQty = Math.Max(0, summary.ExportQty);
                return new OrderMarkingExportPreviewLine(
                    summary.OrderLineId,
                    summary.ItemId,
                    summary.ItemName,
                    summary.Gtin,
                    previewQty,
                    huCodes.Count,
                    huCodes);
            })
            .Where(line => line.Qty > QtyTolerance)
            .ToList();

        return new OrderMarkingExportPreviewResult(
            true,
            lines.Count == 0
                ? "В заказе нет строк для формирования Excel ЧЗ."
                : "Предпросмотр Excel ЧЗ.",
            order.Id,
            order.OrderRef,
            lines.Count,
            lines.Sum(line => line.Qty),
            lines) { Snapshot = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { order.Id, order.OrderRef, order.Status, lines }))) };
    }

    public OrderMarkingExportResult Export(long orderId, DateTime generatedAt,
        IReadOnlyCollection<long>? selectedLineIds = null, string? expectedSnapshot = null)
    {
        if (_data is IReadOnlySnapshotStore snapshots)
            return snapshots.ReadSnapshot(store => new OrderMarkingExportService(store)
                .ExportSnapshot(orderId, generatedAt, selectedLineIds, expectedSnapshot));
        return ExportSnapshot(orderId, generatedAt, selectedLineIds, expectedSnapshot);
    }

    private OrderMarkingExportResult ExportSnapshot(long orderId, DateTime generatedAt,
        IReadOnlyCollection<long>? selectedLineIds, string? expectedSnapshot)
    {
        var preview = PreviewSnapshot(orderId);
        if (!preview.IsSuccess) return OrderMarkingExportResult.Failure(preview.Message);
        if (expectedSnapshot != null && expectedSnapshot != preview.Snapshot)
            return OrderMarkingExportResult.Failure("Заказ изменился. Откройте предпросмотр повторно.");
        var ids = selectedLineIds?.ToHashSet();
        if (ids != null && (ids.Count == 0 || ids.Any(id => !preview.Lines.Any(line => line.OrderLineId == id))))
            return OrderMarkingExportResult.Failure("Выберите существующие строки предпросмотра.");
        var lines = preview.Lines.Where(line => ids == null || ids.Contains(line.OrderLineId)).ToArray();
        if (lines.Length == 0) return OrderMarkingExportResult.Failure("Нет строк для формирования Excel ЧЗ.");
        var rows = lines.Select(line => new MarkingExportRow
            { ItemName = line.ItemName, Gtin = line.Gtin, Qty = line.Qty }).ToArray();
        return new OrderMarkingExportResult(true, "Excel ЧЗ сформирован.",
            OrderExcelWorkbook.Build(rows),
            $"chestny_znak_order_{NormalizeFilePart(preview.OrderRef)}_{generatedAt:yyyyMMdd_HHmmss}.xlsx",
            lines.Length, rows.Length, lines.Sum(line => line.Qty), 0, 0, 0,
            lines.Select(line => new OrderMarkingExportLineSummary(line.OrderLineId, line.ItemId,
                line.ItemName, line.Gtin, line.Qty, 0, 0, line.Qty)).ToArray());
    }

    private IEnumerable<OrderMarkingExportLineSummary> BuildLineSummaries(Order order)
    {
        var orderLines = _data.GetOrderLines(order.Id);
        var shippedByLine = order.Type == OrderType.Customer
            ? _data.GetShippedTotalsByOrderLine(order.Id)
            : new Dictionary<long, double>();
        var reservedByLine = order.Type == OrderType.Customer
            ? CustomerOutboundBoundHuService.BuildUnshippedBoundHuQtyByOrderLine(_data, order.Id)
            : new Dictionary<long, double>();
        var activeProductionPalletQtyByLine = order.Type == OrderType.Customer
            ? BuildActiveProductionPalletQtyByOrderLine(order.Id)
            : new Dictionary<long, double>();

        var markableLines = orderLines
            .Select(line => (Line: line, Item: _data.FindItemById(line.ItemId)))
            .Where(pair => pair.Item?.ItemTypeEnableMarking == true
                           && !string.IsNullOrWhiteSpace(pair.Item.Gtin))
            .ToList();
        if (markableLines.Count == 0)
        {
            yield break;
        }

        foreach (var pair in markableLines)
        {
            var line = pair.Line;
            var item = pair.Item!;
            var requiredQty = Math.Max(0, line.QtyOrdered);
            var productionBaseQty = requiredQty;
            var stockCoveredQty = 0d;
            if (order.Type == OrderType.Customer)
            {
                shippedByLine.TryGetValue(line.Id, out var shippedQty);
                reservedByLine.TryGetValue(line.Id, out var reservedQty);
                stockCoveredQty = Math.Min(requiredQty, Math.Max(0, shippedQty) + Math.Max(0, reservedQty));
                activeProductionPalletQtyByLine.TryGetValue(line.Id, out var activePalletQty);
                productionBaseQty = activePalletQty > QtyTolerance
                    ? Math.Min(activePalletQty, Math.Max(0, requiredQty - stockCoveredQty))
                    : Math.Max(0, requiredQty - stockCoveredQty);
            }

            var coveredQty = stockCoveredQty;
            var exportQty = Math.Max(0, productionBaseQty);

            yield return new OrderMarkingExportLineSummary(
                line.Id,
                line.ItemId,
                item.Name,
                item.Gtin!.Trim(),
                requiredQty,
                coveredQty,
                0,
                exportQty);
        }
    }

    private Dictionary<long, double> BuildActiveProductionPalletQtyByOrderLine(long orderId)
    {
        var result = new Dictionary<long, double>();
        foreach (var pallet in EnumerateActiveProductionPallets(orderId))
        {
            if (pallet.Lines.Count > 0)
            {
                foreach (var line in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                {
                    AddQty(result, line.OrderLineId!.Value, Math.Max(0, line.PlannedQty));
                }

                continue;
            }

            if (pallet.OrderLineId.HasValue)
            {
                AddQty(result, pallet.OrderLineId.Value, Math.Max(0, pallet.PlannedQty));
            }
        }

        return result;
    }

    private Dictionary<long, IReadOnlyList<string>> BuildProductionHuCodesByOrderLine(long orderId)
    {
        var result = new Dictionary<long, List<string>>();
        foreach (var pallet in EnumerateActiveProductionPallets(orderId))
        {
            if (string.IsNullOrWhiteSpace(pallet.HuCode))
            {
                continue;
            }

            var huCode = pallet.HuCode.Trim();
            if (pallet.Lines.Count > 0)
            {
                foreach (var line in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                {
                    AddHuCode(result, line.OrderLineId!.Value, huCode);
                }

                continue;
            }

            if (pallet.OrderLineId.HasValue)
            {
                AddHuCode(result, pallet.OrderLineId.Value, huCode);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private IEnumerable<ProductionPallet> EnumerateActiveProductionPallets(long orderId)
    {
        foreach (var doc in _data.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in _data.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status)))
            {
                yield return pallet;
            }
        }
    }

    private static void AddHuCode(IDictionary<long, List<string>> codesByLine, long orderLineId, string huCode)
    {
        if (!codesByLine.TryGetValue(orderLineId, out var codes))
        {
            codes = new List<string>();
            codesByLine[orderLineId] = codes;
        }

        codes.Add(huCode);
    }

    private static void AddQty(IDictionary<long, double> totals, long orderLineId, double qty)
    {
        if (qty <= QtyTolerance)
        {
            return;
        }

        totals[orderLineId] = totals.TryGetValue(orderLineId, out var current)
            ? current + qty
            : qty;
    }

    private static string NormalizeFilePart(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "order" : result;
    }

}
