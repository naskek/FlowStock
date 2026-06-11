using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderLineHuFateDisplayBuilder
{
    public const string OnStockFateCode = "ON_STOCK";
    public const string ReservedFateCode = "RESERVED";
    public const string ShippedFateCode = "SHIPPED";
    public const int FilledSortOrder = 2;
    public const int ReservedSortOrder = 3;
    public const int ShippedSortOrder = 4;

    public static Dictionary<long, OrderLineHuDisplayEntry[]> BuildByOrder(IDataStore store, long orderId)
    {
        var orders = store.GetOrders().ToDictionary(order => order.Id);
        if (!orders.ContainsKey(orderId))
        {
            return new Dictionary<long, OrderLineHuDisplayEntry[]>();
        }

        var docs = store.GetDocs();
        var stockByHu = store.GetHuStockRows()
            .Where(row => !string.IsNullOrWhiteSpace(NormalizeHu(row.HuCode)))
            .GroupBy(row => new HuKey(row.ItemId, NormalizeHu(row.HuCode)!))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        var sources = BuildSources(store, docs, orders);
        var reservations = BuildReservations(store, orders, stockByHu);
        var shipments = BuildShipments(store, docs, orders);
        var latestShipmentByHu = shipments
            .GroupBy(row => row.Key)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(row => row.ClosedAt ?? DateTime.MinValue)
                .ThenByDescending(row => row.CreatedAt)
                .ThenByDescending(row => row.DocId)
                .First());
        var reservationByHu = reservations
            .GroupBy(row => row.Key)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(row => row.TargetOrderId)
                .ThenBy(row => row.TargetOrderLineId)
                .First());
        var rows = new Dictionary<long, Dictionary<string, OrderLineHuDisplayEntry>>();

        foreach (var source in sources.Values.Where(source => source.SourceOrderId == orderId))
        {
            var hasPositiveStock = stockByHu.GetValueOrDefault(source.Key) > StockQuantityRules.QtyTolerance;
            if (latestShipmentByHu.TryGetValue(source.Key, out var shipment))
            {
                var sameOrder = shipment.TargetOrderId == source.SourceOrderId;
                var targetOrderRef = OrderRef(orders, shipment.TargetOrderId);
                var fateLabel = sameOrder ? "отгружено" : $"→ отгружено заказ {targetOrderRef}";
                Add(rows, source.SourceOrderLineId, new OrderLineHuDisplayEntry(
                    source.Key.HuCode,
                    sameOrder ? "отгружено" : "наполнено",
                    sameOrder ? shipment.Qty : source.Qty,
                    IsWarehouseBound: false,
                    SortOrder: ShippedSortOrder,
                    sameOrder ? null : fateLabel,
                    FateCode: ShippedFateCode,
                    FateLabel: fateLabel,
                    FateOrderRef: targetOrderRef,
                    FateDocRef: shipment.DocRef,
                    FateQty: shipment.Qty));
            }
            else if (reservationByHu.TryGetValue(source.Key, out var reservation))
            {
                var targetOrderRef = OrderRef(orders, reservation.TargetOrderId);
                var sameOrder = reservation.TargetOrderId == source.SourceOrderId;
                var fateLabel = sameOrder ? "резерв этого заказа" : $"→ резерв заказ {targetOrderRef}";
                Add(rows, source.SourceOrderLineId, new OrderLineHuDisplayEntry(
                    source.Key.HuCode,
                    "наполнено",
                    source.Qty,
                    IsWarehouseBound: false,
                    SortOrder: sameOrder ? FilledSortOrder : ReservedSortOrder,
                    sameOrder ? null : fateLabel,
                    FateCode: ReservedFateCode,
                    FateLabel: fateLabel,
                    FateOrderRef: targetOrderRef,
                    FateQty: reservation.Qty));
            }
            else if (hasPositiveStock)
            {
                var stockQty = stockByHu.GetValueOrDefault(source.Key);
                Add(rows, source.SourceOrderLineId, new OrderLineHuDisplayEntry(
                    source.Key.HuCode,
                    "наполнено",
                    source.Qty,
                    IsWarehouseBound: false,
                    SortOrder: FilledSortOrder,
                    FateCode: OnStockFateCode,
                    FateLabel: "на складе",
                    FateQty: stockQty));
            }
        }

        foreach (var shipment in shipments
                     .Where(row => row.TargetOrderId == orderId)
                     .GroupBy(row => (row.Key, row.TargetOrderId, row.TargetOrderLineId))
                     .Select(group => group
                         .OrderByDescending(row => row.ClosedAt ?? DateTime.MinValue)
                         .ThenByDescending(row => row.CreatedAt)
                         .ThenByDescending(row => row.DocId)
                         .First() with { Qty = group.Sum(row => row.Qty) }))
        {
            sources.TryGetValue(shipment.Key, out var source);
            var sourceOrderRef = source == null ? null : OrderRef(orders, source.SourceOrderId);
            var fateLabel = source == null || source.SourceOrderId == shipment.TargetOrderId
                ? "отгружено"
                : $"← выпуск заказ {sourceOrderRef}";
            Add(rows, shipment.TargetOrderLineId, new OrderLineHuDisplayEntry(
                shipment.Key.HuCode,
                "отгружено",
                shipment.Qty,
                IsWarehouseBound: false,
                SortOrder: ShippedSortOrder,
                source == null || source.SourceOrderId == shipment.TargetOrderId ? null : fateLabel,
                FateCode: ShippedFateCode,
                FateLabel: fateLabel,
                FateOrderRef: OrderRef(orders, shipment.TargetOrderId),
                FateDocRef: shipment.DocRef,
                FateQty: shipment.Qty));
        }

        foreach (var reservation in reservations.Where(row => row.TargetOrderId == orderId))
        {
            if (shipments.Any(row => row.TargetOrderId == reservation.TargetOrderId
                                     && row.TargetOrderLineId == reservation.TargetOrderLineId
                                     && row.Key == reservation.Key))
            {
                continue;
            }

            sources.TryGetValue(reservation.Key, out var source);
            var sameOrder = source?.SourceOrderId == reservation.TargetOrderId;
            var sourceOrderRef = source == null ? null : OrderRef(orders, source.SourceOrderId);
            var fateLabel = source == null || sameOrder ? "резерв этого заказа" : $"← выпуск заказ {sourceOrderRef}";
            Add(rows, reservation.TargetOrderLineId, new OrderLineHuDisplayEntry(
                reservation.Key.HuCode,
                sameOrder ? "наполнено" : "резерв",
                sameOrder ? source!.Qty : reservation.Qty,
                IsWarehouseBound: false,
                SortOrder: sameOrder ? FilledSortOrder : ReservedSortOrder,
                source == null || sameOrder ? null : fateLabel,
                FateCode: ReservedFateCode,
                FateLabel: fateLabel,
                FateOrderRef: OrderRef(orders, reservation.TargetOrderId),
                FateQty: reservation.Qty));
        }

        return rows.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Values
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static Dictionary<HuKey, SourceHu> BuildSources(
        IDataStore store,
        IReadOnlyList<Doc> docs,
        IReadOnlyDictionary<long, Order> orders)
    {
        var result = new Dictionary<HuKey, SourceHu>();

        foreach (var doc in docs.Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                var sourceOrderId = pallet.OrderId ?? doc.OrderId;
                var huCode = NormalizeHu(pallet.HuCode);
                if (!sourceOrderId.HasValue || !orders.ContainsKey(sourceOrderId.Value) || huCode == null)
                {
                    continue;
                }

                var components = pallet.Lines.Count > 0
                    ? pallet.Lines
                    : [new ProductionPalletComponentLine
                    {
                        OrderLineId = pallet.OrderLineId,
                        ItemId = pallet.ItemId,
                        PlannedQty = pallet.PlannedQty,
                        FilledQty = pallet.PlannedQty
                    }];
                foreach (var component in components.Where(component => component.OrderLineId.HasValue))
                {
                    var qty = component.FilledQty > StockQuantityRules.QtyTolerance
                        ? component.FilledQty
                        : component.PlannedQty;
                    AddSource(result, new SourceHu(
                        new HuKey(component.ItemId, huCode),
                        sourceOrderId.Value,
                        component.OrderLineId!.Value,
                        qty,
                        Priority: 0,
                        SortAt: pallet.FilledAt ?? pallet.CreatedAt,
                        SortId: pallet.Id));
                }
            }
        }

        foreach (var doc in docs.Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed && doc.OrderId.HasValue))
        {
            foreach (var line in store.GetDocLines(doc.Id).Where(line => line.OrderLineId.HasValue))
            {
                var huCode = NormalizeHu(line.ToHu);
                if (huCode == null)
                {
                    continue;
                }

                var ledgerQty = store.GetLedgerQtyByDocItemHu(doc.Id, line.ItemId, huCode);
                if (ledgerQty <= StockQuantityRules.QtyTolerance)
                {
                    continue;
                }

                AddSource(result, new SourceHu(
                    new HuKey(line.ItemId, huCode),
                    doc.OrderId!.Value,
                    line.OrderLineId!.Value,
                    Math.Min(line.Qty, ledgerQty),
                    Priority: 1,
                    SortAt: doc.ClosedAt ?? doc.CreatedAt,
                    SortId: doc.Id));
            }
        }

        return result;
    }

    private static List<TargetReservation> BuildReservations(
        IDataStore store,
        IReadOnlyDictionary<long, Order> orders,
        IReadOnlyDictionary<HuKey, double> stockByHu)
    {
        var rows = new List<TargetReservation>();
        foreach (var order in orders.Values.Where(order => order.Type == OrderType.Customer
                                                           && order.Status is not OrderStatus.Cancelled
                                                           && order.Status is not OrderStatus.Shipped
                                                           && order.Status is not OrderStatus.Merged))
        {
            rows.AddRange(store.GetOrderReceiptPlanLines(order.Id)
                .Where(line => line.OrderLineId > 0 && line.QtyPlanned > StockQuantityRules.QtyTolerance)
                .Select(line => new { Line = line, HuCode = NormalizeHu(line.ToHu) })
                .Where(row => row.HuCode != null)
                .Select(row => new TargetReservation(
                    new HuKey(row.Line.ItemId, row.HuCode!),
                    order.Id,
                    row.Line.OrderLineId,
                    row.Line.QtyPlanned))
                .Where(row => stockByHu.GetValueOrDefault(row.Key) > StockQuantityRules.QtyTolerance));
        }

        return rows
            .GroupBy(row => (row.Key, row.TargetOrderId, row.TargetOrderLineId))
            .Select(group => group.First() with { Qty = group.Sum(row => row.Qty) })
            .ToList();
    }

    private static List<TargetShipment> BuildShipments(
        IDataStore store,
        IReadOnlyList<Doc> docs,
        IReadOnlyDictionary<long, Order> orders)
    {
        var rows = new List<TargetShipment>();
        foreach (var doc in docs.Where(doc => doc.Type == DocType.Outbound
                                              && doc.Status == DocStatus.Closed
                                              && doc.OrderId.HasValue
                                              && orders.TryGetValue(doc.OrderId.Value, out var order)
                                              && order.Type == OrderType.Customer))
        {
            rows.AddRange(store.GetDocLines(doc.Id)
                .Where(line => line.OrderLineId.HasValue && line.Qty > StockQuantityRules.QtyTolerance)
                .Select(line => new { Line = line, HuCode = NormalizeHu(line.FromHu) })
                .Where(row => row.HuCode != null)
                .Select(row => new TargetShipment(
                    new HuKey(row.Line.ItemId, row.HuCode!),
                    doc.OrderId!.Value,
                    row.Line.OrderLineId!.Value,
                    row.Line.Qty,
                    doc.Id,
                    doc.DocRef,
                    doc.ClosedAt,
                    doc.CreatedAt)));
        }

        return rows
            .GroupBy(row => (row.Key, row.TargetOrderId, row.TargetOrderLineId, row.DocId))
            .Select(group => group.First() with { Qty = group.Sum(row => row.Qty) })
            .ToList();
    }

    private static void AddSource(Dictionary<HuKey, SourceHu> rows, SourceHu candidate)
    {
        if (!rows.TryGetValue(candidate.Key, out var current)
            || candidate.Priority < current.Priority
            || candidate.Priority == current.Priority && (candidate.SortAt, candidate.SortId).CompareTo((current.SortAt, current.SortId)) < 0)
        {
            rows[candidate.Key] = candidate;
        }
    }

    private static void Add(
        Dictionary<long, Dictionary<string, OrderLineHuDisplayEntry>> rows,
        long orderLineId,
        OrderLineHuDisplayEntry candidate)
    {
        if (!rows.TryGetValue(orderLineId, out var byHu))
        {
            byHu = new Dictionary<string, OrderLineHuDisplayEntry>(StringComparer.OrdinalIgnoreCase);
            rows[orderLineId] = byHu;
        }

        if (!byHu.TryGetValue(candidate.HuCode, out var current)
            || candidate.SortOrder > current.SortOrder
            || candidate.SortOrder == current.SortOrder && string.Equals(candidate.Label, "отгружено", StringComparison.OrdinalIgnoreCase))
        {
            byHu[candidate.HuCode] = candidate;
        }
    }

    private static string OrderRef(IReadOnlyDictionary<long, Order> orders, long orderId)
    {
        return orders.TryGetValue(orderId, out var order) && !string.IsNullOrWhiteSpace(order.OrderRef)
            ? order.OrderRef.Trim()
            : orderId.ToString();
    }

    private static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private sealed record SourceHu(
        HuKey Key,
        long SourceOrderId,
        long SourceOrderLineId,
        double Qty,
        int Priority,
        DateTime SortAt,
        long SortId);

    private sealed record TargetReservation(HuKey Key, long TargetOrderId, long TargetOrderLineId, double Qty);

    private sealed record TargetShipment(
        HuKey Key,
        long TargetOrderId,
        long TargetOrderLineId,
        double Qty,
        long DocId,
        string DocRef,
        DateTime? ClosedAt,
        DateTime CreatedAt);

    private sealed record HuKey(long ItemId, string HuCode);
}
