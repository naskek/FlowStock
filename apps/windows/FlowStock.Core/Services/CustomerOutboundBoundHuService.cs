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
                group => group
                    .OrderBy(row => row.LocationId)
                    .First(),
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

            if (!stockByHuItem.TryGetValue(BuildHuItemKey(huCode, planLine.ItemId), out var stockRow))
            {
                continue;
            }

            var locationId = (long?)stockRow.LocationId;
            var locationCode = locationsById.TryGetValue(stockRow.LocationId, out var stockLocationCode)
                ? stockLocationCode
                : stockRow.LocationId.ToString();

            result.Add(new CustomerOutboundBoundHuLine
            {
                OrderLineId = planLine.OrderLineId,
                ItemId = planLine.ItemId,
                ItemName = planLine.ItemName,
                Qty = remainingQty,
                HuCode = huCode,
                FromLocationId = locationId,
                FromLocationCode = locationCode
            });
        }

        return result;
    }

    public static IReadOnlyDictionary<long, double> BuildUnshippedBoundHuQtyByOrderLine(
        IDataStore store,
        long orderId)
    {
        return GetUnshippedBoundHuLines(store, orderId)
            .GroupBy(line => line.OrderLineId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(line => Math.Max(0, line.Qty)));
    }

    public static IReadOnlyList<CustomerOutboundBoundHuLine> GetUnshippedFilledProductionPalletHuLines(
        IDataStore store,
        long orderId)
    {
        var order = store.GetOrder(orderId);
        if (order == null || order.Type != OrderType.Customer)
        {
            return Array.Empty<CustomerOutboundBoundHuLine>();
        }

        var shipmentRemainingByOrderLine = store.GetOrderShipmentRemaining(orderId)
            .Where(line => line.QtyRemaining > QtyTolerance)
            .ToDictionary(line => line.OrderLineId, line => line.QtyRemaining);
        if (shipmentRemainingByOrderLine.Count == 0)
        {
            return Array.Empty<CustomerOutboundBoundHuLine>();
        }

        var shippedByOrderLineHu = BuildShippedQtyByOrderLineAndHu(store, orderId);
        var locationsById = store.GetLocations().ToDictionary(location => location.Id, location => location.Code);
        var stockByHuItem = BuildStockByHuItem(store);
        var result = new List<CustomerOutboundBoundHuLine>();

        foreach (var pallet in GetFilledProductionPalletsForCustomerOrder(store, orderId))
        {
            var huCode = NormalizeHu(pallet.HuCode);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            foreach (var palletLine in ExpandProductionPalletOutboundLines(pallet))
            {
                if (!shipmentRemainingByOrderLine.TryGetValue(palletLine.OrderLineId, out var shipmentRemaining))
                {
                    continue;
                }

                var shippedKey = (palletLine.OrderLineId, huCode);
                if (shippedByOrderLineHu.TryGetValue(shippedKey, out var shippedQty)
                    && shippedQty >= shipmentRemaining - QtyTolerance)
                {
                    continue;
                }

                if (!stockByHuItem.TryGetValue(BuildHuItemKey(huCode, palletLine.ItemId), out var stockRow)
                    || stockRow.Qty <= QtyTolerance)
                {
                    continue;
                }

                var qty = Math.Min(stockRow.Qty, shipmentRemaining);
                if (qty <= QtyTolerance)
                {
                    continue;
                }

                var locationId = (long?)stockRow.LocationId;
                if (!locationId.HasValue && pallet.ToLocationId.HasValue)
                {
                    locationId = pallet.ToLocationId;
                }

                var locationCode = locationId.HasValue
                    && locationsById.TryGetValue(locationId.Value, out var palletLocationCode)
                    ? palletLocationCode
                    : pallet.ToLocationCode ?? locationId?.ToString();

                result.Add(new CustomerOutboundBoundHuLine
                {
                    OrderLineId = palletLine.OrderLineId,
                    ItemId = palletLine.ItemId,
                    ItemName = palletLine.ItemName,
                    Qty = qty,
                    HuCode = huCode,
                    FromLocationId = locationId,
                    FromLocationCode = locationCode
                });
            }
        }

        return result;
    }

    public static IReadOnlyList<CustomerOutboundBoundHuLine> GetUnshippedOutboundHuLines(IDataStore store, long orderId)
    {
        var merged = new Dictionary<string, CustomerOutboundBoundHuLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in GetUnshippedBoundHuLines(store, orderId))
        {
            MergeOutboundHuLine(merged, line);
        }

        foreach (var line in GetUnshippedFilledProductionPalletHuLines(store, orderId))
        {
            MergeOutboundHuLine(merged, line);
        }

        return merged.Values
            .OrderBy(line => line.HuCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.OrderLineId)
            .ThenBy(line => line.ItemId)
            .ToArray();
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

        foreach (var boundLine in GetUnshippedOutboundHuLines(store, order.Id))
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

    public static Dictionary<long, HashSet<string>> BuildOrderBoundHuByItem(IDataStore store, long orderId)
    {
        var result = new Dictionary<long, HashSet<string>>();
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed))
        {
            foreach (var line in store.GetDocLines(doc.Id))
            {
                if (line.Qty <= QtyTolerance)
                {
                    continue;
                }

                var huCode = NormalizeHu(line.ToHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                AddBoundHu(result, line.ItemId, huCode);
            }
        }

        foreach (var line in store.GetOrderReceiptPlanLines(orderId))
        {
            if (line.QtyPlanned <= QtyTolerance)
            {
                continue;
            }

            var huCode = NormalizeHu(line.ToHu);
            if (string.IsNullOrWhiteSpace(huCode) || !HasPositiveHuBalance(store, line.ItemId, huCode))
            {
                continue;
            }

            AddBoundHu(result, line.ItemId, huCode);
        }

        foreach (var pallet in GetFilledProductionPalletsForCustomerOrder(store, orderId))
        {
            var huCode = NormalizeHu(pallet.HuCode);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            foreach (var palletLine in ExpandProductionPalletOutboundLines(pallet))
            {
                if (!HasPositiveHuBalance(store, palletLine.ItemId, huCode))
                {
                    continue;
                }

                AddBoundHu(result, palletLine.ItemId, huCode);
            }
        }

        return result;
    }

    public static bool HasReceiptProductionNeed(IDataStore store, long customerOrderId, bool includeReservedStock = true)
    {
        var order = store.GetOrder(customerOrderId);
        if (order == null || order.Type != OrderType.Customer)
        {
            return false;
        }

        return OrderReceiptRemainingCalculator.GetRemaining(store, order, includeReservedStock)
            .Any(line => line.QtyRemaining > QtyTolerance);
    }

    private static Dictionary<string, HuStockRow> BuildStockByHuItem(IDataStore store)
    {
        return store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .GroupBy(row => BuildHuItemKey(row.HuCode, row.ItemId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(row => row.LocationId)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<ProductionPallet> GetFilledProductionPalletsForCustomerOrder(
        IDataStore store,
        long orderId)
    {
        var order = store.GetOrder(orderId);
        if (order == null || order.Type != OrderType.Customer)
        {
            return Array.Empty<ProductionPallet>();
        }

        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .OrderBy(doc => doc.Id)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => pallet.OrderId == orderId
                             && string.Equals(
                                 pallet.Status,
                                 ProductionPalletStatus.Filled,
                                 StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(long OrderLineId, long ItemId, string ItemName)> ExpandProductionPalletOutboundLines(
        ProductionPallet pallet)
    {
        if (pallet.Lines.Count > 0)
        {
            foreach (var line in pallet.Lines)
            {
                if (!line.OrderLineId.HasValue || line.ItemId <= 0)
                {
                    continue;
                }

                yield return (line.OrderLineId.Value, line.ItemId, line.ItemName);
            }

            yield break;
        }

        if (!pallet.OrderLineId.HasValue || pallet.ItemId <= 0)
        {
            yield break;
        }

        yield return (pallet.OrderLineId.Value, pallet.ItemId, pallet.ItemName);
    }

    private static void MergeOutboundHuLine(
        IDictionary<string, CustomerOutboundBoundHuLine> merged,
        CustomerOutboundBoundHuLine line)
    {
        var key = BuildOutboundLineKey(line);
        if (!merged.TryGetValue(key, out var existing))
        {
            merged[key] = line;
            return;
        }

        if (line.Qty > existing.Qty + QtyTolerance)
        {
            merged[key] = line;
        }
    }

    private static string BuildOutboundLineKey(CustomerOutboundBoundHuLine line)
    {
        return $"{line.OrderLineId}|{NormalizeHu(line.HuCode)}|{line.ItemId}";
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

    private static void AddBoundHu(IDictionary<long, HashSet<string>> result, long itemId, string huCode)
    {
        if (!result.TryGetValue(itemId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result[itemId] = set;
        }

        set.Add(huCode);
    }

    private static bool HasPositiveHuBalance(IDataStore store, long itemId, string huCode)
    {
        return store.GetHuStockRows()
            .Where(row => row.ItemId == itemId)
            .Where(row => string.Equals(NormalizeHu(row.HuCode), huCode, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty) > QtyTolerance;
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }
}
