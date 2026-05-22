using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class CustomerOutboundBoundHuLine
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long? FromLocationId { get; init; }
    public string? FromLocationCode { get; init; }
}

public static class CustomerOutboundBoundHuService
{
    private const double QtyTolerance = 0.000001d;

    public static IReadOnlyList<CustomerOutboundBoundHuLine> GetUnshippedBoundHuLines(IDataStore store, long orderId)
    {
        var order = store.GetOrder(orderId);
        if (order == null || order.Type != OrderType.Customer)
        {
            return Array.Empty<CustomerOutboundBoundHuLine>();
        }

        var shippedByOrderLineHu = BuildShippedQtyByOrderLineAndHu(store, orderId);
        var locationsById = store.GetLocations().ToDictionary(location => location.Id, location => location.Code);
        var stockByHuItem = store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .GroupBy(row => BuildHuItemKey(row.HuCode, row.ItemId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.OrderBy(row => row.LocationId).First();
                    return (first.LocationId, Qty: group.Sum(row => row.Qty));
                },
                StringComparer.OrdinalIgnoreCase);

        var result = new List<CustomerOutboundBoundHuLine>();
        foreach (var planLine in store.GetOrderReceiptPlanLines(orderId)
                     .Where(line => line.QtyPlanned > QtyTolerance && !string.IsNullOrWhiteSpace(NormalizeHu(line.ToHu)))
                     .OrderBy(line => line.SortOrder)
                     .ThenBy(line => line.Id))
        {
            var huCode = NormalizeHu(planLine.ToHu)!;
            var shippedKey = (planLine.OrderLineId, huCode);
            var shippedQty = shippedByOrderLineHu.TryGetValue(shippedKey, out var qty) ? qty : 0d;
            var remainingQty = planLine.QtyPlanned - shippedQty;
            if (remainingQty <= QtyTolerance)
            {
                continue;
            }

            var stockKey = BuildHuItemKey(huCode, planLine.ItemId);
            if (!stockByHuItem.TryGetValue(stockKey, out var stockRow)
                || stockRow.Qty <= QtyTolerance)
            {
                continue;
            }

            var physicalQty = Math.Min(remainingQty, stockRow.Qty);
            if (physicalQty <= QtyTolerance)
            {
                continue;
            }
            stockByHuItem[stockKey] = (stockRow.LocationId, stockRow.Qty - physicalQty);

            long? locationId = stockRow.LocationId;
            string? locationCode = planLine.ToLocationCode;
            if (locationsById.TryGetValue(stockRow.LocationId, out var stockLocationCode))
            {
                locationCode = stockLocationCode;
            }

            result.Add(new CustomerOutboundBoundHuLine
            {
                OrderLineId = planLine.OrderLineId,
                ItemId = planLine.ItemId,
                ItemName = planLine.ItemName,
                Qty = physicalQty,
                HuCode = huCode,
                FromLocationId = locationId,
                FromLocationCode = locationCode
            });
        }

        return result;
    }

    public static int SyncDraftOutboundFromBoundHu(IDataStore store, long docId, bool replaceAll = false)
    {
        var doc = store.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Type != DocType.Outbound)
        {
            throw new InvalidOperationException("Документ не является отгрузкой.");
        }

        if (doc.Status != DocStatus.Draft)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        if (!doc.OrderId.HasValue)
        {
            throw new InvalidOperationException("Для отгрузки не указан заказ.");
        }

        var order = store.GetOrder(doc.OrderId.Value) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Type != OrderType.Customer)
        {
            return 0;
        }

        var addedLines = 0;
        if (replaceAll)
        {
            store.DeleteDocLines(docId);
        }

        var existingKeys = store.GetDocLines(docId)
            .Select(line => BuildLineKey(line.OrderLineId, line.FromHu))
            .Where(key => key.HasValue)
            .Select(key => key!.Value)
            .ToHashSet();

        foreach (var boundLine in GetUnshippedBoundHuLines(store, order.Id))
        {
            var key = (boundLine.OrderLineId, boundLine.HuCode);
            if (existingKeys.Contains(key))
            {
                continue;
            }

            store.AddDocLine(new DocLine
            {
                DocId = docId,
                OrderLineId = boundLine.OrderLineId,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ItemId = boundLine.ItemId,
                Qty = boundLine.Qty,
                QtyInput = null,
                UomCode = null,
                FromLocationId = boundLine.FromLocationId,
                ToLocationId = null,
                FromHu = boundLine.HuCode,
                ToHu = null
            });
            existingKeys.Add(key);
            addedLines++;
        }

        return addedLines;
    }

    public static bool HasReceiptProductionNeed(IDataStore store, long customerOrderId, bool includeReservedStock = true)
    {
        var order = store.GetOrder(customerOrderId);
        if (order == null || order.Type != OrderType.Customer)
        {
            return false;
        }

        if (!includeReservedStock)
        {
            return OrderReceiptRemainingCalculator.GetRemaining(store, order, includeReservedStock: false)
                .Any(line => line.QtyRemaining > QtyTolerance);
        }

        var physicallyCoveredByOrderLine = GetUnshippedBoundHuLines(store, customerOrderId)
            .GroupBy(line => line.OrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));

        return OrderReceiptRemainingCalculator.GetRemaining(store, order, includeReservedStock: false)
            .Any(line =>
            {
                physicallyCoveredByOrderLine.TryGetValue(line.OrderLineId, out var covered);
                return Math.Max(0, line.QtyRemaining - covered) > QtyTolerance;
            });
    }

    private static Dictionary<(long OrderLineId, string HuCode), double> BuildShippedQtyByOrderLineAndHu(
        IDataStore store,
        long orderId)
    {
        var result = new Dictionary<(long, string), double>();
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Closed))
        {
            foreach (var line in store.GetDocLines(doc.Id))
            {
                if (line.Qty <= QtyTolerance || !line.OrderLineId.HasValue)
                {
                    continue;
                }

                var huCode = NormalizeHu(line.FromHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                var key = (line.OrderLineId.Value, huCode);
                result[key] = result.TryGetValue(key, out var current) ? current + line.Qty : line.Qty;
            }
        }

        return result;
    }

    private static (long OrderLineId, string HuCode)? BuildLineKey(long? orderLineId, string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (!orderLineId.HasValue || string.IsNullOrWhiteSpace(normalizedHu))
        {
            return null;
        }

        return (orderLineId.Value, normalizedHu);
    }

    private static string BuildHuItemKey(string? huCode, long itemId)
    {
        return $"{NormalizeHu(huCode)}|{itemId}";
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }
}
