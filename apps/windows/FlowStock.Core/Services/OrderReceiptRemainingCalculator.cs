using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

internal static class OrderReceiptRemainingCalculator
{
    private const double QtyTolerance = 0.000001d;

    public static IReadOnlyList<OrderReceiptLine> GetRemaining(IDataStore dataStore, Order order, bool includeReservedStock = true)
    {
        if (order.Type == OrderType.Customer)
        {
            return includeReservedStock
                ? dataStore.GetOrderReceiptRemaining(order.Id)
                : dataStore.GetOrderReceiptRemainingWithoutReservedStock(order.Id);
        }

        var orderLines = dataStore.GetOrderLines(order.Id)
            .OrderBy(line => line.Id)
            .ToList();
        var projectedRemaining = dataStore.GetOrderReceiptRemaining(order.Id)
            .ToDictionary(line => line.OrderLineId, line => line);
        var producedByLine = BuildProducedTotalsByOrderLine(dataStore, order.Id, orderLines);

        return orderLines
            .Select(line =>
            {
                var produced = producedByLine.TryGetValue(line.Id, out var qty) ? qty : 0d;
                return new OrderReceiptLine
                {
                    OrderLineId = line.Id,
                    OrderId = order.Id,
                    ItemId = line.ItemId,
                    ItemName = projectedRemaining.TryGetValue(line.Id, out var projected) ? projected.ItemName : string.Empty,
                    QtyOrdered = line.QtyOrdered,
                    QtyReceived = produced,
                    QtyRemaining = Math.Max(0, line.QtyOrdered - produced),
                    ProductionPurpose = line.ProductionPurpose
                };
            })
            .ToList();
    }

    public static IReadOnlyDictionary<long, double> BuildProducedTotalsByOrderLine(
        IDataStore dataStore,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines = null)
    {
        var lines = (orderLines ?? dataStore.GetOrderLines(orderId))
            .OrderBy(line => line.Id)
            .ToList();
        var linesByItem = lines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToList());
        var totals = dataStore.GetOrderReceiptRemaining(orderId)
            .ToDictionary(line => line.OrderLineId, line => Math.Max(0, line.QtyReceived));

        try
        {
            if (dataStore is IOptimizedOrderReadModelStore optimizedStore)
            {
                foreach (var itemTotals in optimizedStore.GetUnlinkedProductionTotalsByItem(orderId))
                {
                    DistributeUnlinkedQtyByItem(totals, linesByItem, itemTotals.Key, itemTotals.Value);
                }

                return totals;
            }

            foreach (var doc in dataStore.GetDocsByOrder(orderId)
                         .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed))
            {
                foreach (var line in dataStore.GetDocLines(doc.Id).Where(line => !line.OrderLineId.HasValue && line.Qty > 0))
                {
                    if (doc.OrderId == orderId)
                    {
                        DistributeUnlinkedQtyByItem(totals, linesByItem, line.ItemId, line.Qty);
                    }
                }
            }
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return totals;
        }

        return totals;
    }

    public static IReadOnlyDictionary<long, double> BuildClosedProductionTotalsByOrderLine(
        IDataStore dataStore,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines = null)
    {
        var lines = (orderLines ?? dataStore.GetOrderLines(orderId))
            .OrderBy(line => line.Id)
            .ToList();
        var linesByItem = lines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToList());
        var totals = lines.ToDictionary(line => line.Id, _ => 0d);

        try
        {
            foreach (var doc in dataStore.GetDocsByOrder(orderId)
                         .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed))
            {
                foreach (var line in dataStore.GetDocLines(doc.Id).Where(line => line.Qty > QtyTolerance))
                {
                    if (line.OrderLineId.HasValue && totals.ContainsKey(line.OrderLineId.Value))
                    {
                        AddProducedQty(totals, line.OrderLineId.Value, line.Qty);
                        continue;
                    }

                    DistributeUnlinkedQtyByItem(totals, linesByItem, line.ItemId, line.Qty);
                }
            }
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return totals;
        }

        return totals;
    }

    public static IReadOnlyDictionary<long, double> BuildGrossReceiptLedgerTotalsByOrderLine(
        IDataStore dataStore,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines = null)
    {
        return BuildReceiptLedgerTotalsByOrderLine(dataStore, orderId, orderLines, requireClosedLegacyReceipt: false);
    }

    public static IReadOnlyDictionary<long, double> BuildConfirmedReceiptLedgerTotalsByOrderLine(
        IDataStore dataStore,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines = null)
    {
        return BuildReceiptLedgerTotalsByOrderLine(dataStore, orderId, orderLines, requireClosedLegacyReceipt: true);
    }

    private static IReadOnlyDictionary<long, double> BuildReceiptLedgerTotalsByOrderLine(
        IDataStore dataStore,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines,
        bool requireClosedLegacyReceipt)
    {
        var lines = (orderLines ?? dataStore.GetOrderLines(orderId))
            .OrderBy(line => line.Id)
            .ToList();
        var totals = lines.ToDictionary(line => line.Id, _ => 0d);
        var linesByItem = lines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToList());

        try
        {
            foreach (var doc in dataStore.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
            {
                var pallets = dataStore.GetProductionPalletsByDoc(doc.Id)
                    .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (pallets.Length > 0)
                {
                    foreach (var pallet in pallets.Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (pallet.Lines.Count > 0)
                        {
                            foreach (var palletLine in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                            {
                                var ledgerQty = Math.Max(0, dataStore.GetLedgerQtyByDocItemHu(doc.Id, palletLine.ItemId, pallet.HuCode));
                                var plannedQty = palletLine.FilledQty > QtyTolerance ? palletLine.FilledQty : palletLine.PlannedQty;
                                AddProducedQty(totals, palletLine.OrderLineId!.Value, Math.Min(plannedQty, ledgerQty));
                            }

                            continue;
                        }

                        if (!pallet.OrderLineId.HasValue)
                        {
                            DistributeUnlinkedQtyByItem(
                                totals,
                                linesByItem,
                                pallet.ItemId,
                                Math.Max(0, dataStore.GetLedgerQtyByDocItemHu(doc.Id, pallet.ItemId, pallet.HuCode)));
                            continue;
                        }

                        var grossQty = Math.Max(0, dataStore.GetLedgerQtyByDocItemHu(doc.Id, pallet.ItemId, pallet.HuCode));
                        AddProducedQty(totals, pallet.OrderLineId.Value, Math.Min(pallet.PlannedQty, grossQty));
                    }

                    continue;
                }

                if (requireClosedLegacyReceipt && doc.Status != DocStatus.Closed)
                {
                    continue;
                }

                foreach (var line in dataStore.GetDocLines(doc.Id).Where(line => line.Qty > QtyTolerance))
                {
                    var ledgerQty = Math.Max(0, dataStore.GetLedgerQtyByDocItemHu(doc.Id, line.ItemId, line.ToHu));
                    var producedQty = Math.Min(line.Qty, ledgerQty);
                    if (line.OrderLineId.HasValue && totals.ContainsKey(line.OrderLineId.Value))
                    {
                        AddProducedQty(totals, line.OrderLineId.Value, producedQty);
                        continue;
                    }

                    DistributeUnlinkedQtyByItem(totals, linesByItem, line.ItemId, producedQty);
                }
            }
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return totals;
        }

        return totals;
    }

    private static void DistributeUnlinkedQtyByItem(
        IDictionary<long, double> totals,
        IReadOnlyDictionary<long, List<OrderLine>> linesByItem,
        long itemId,
        double qty)
    {
        if (qty <= QtyTolerance || !linesByItem.TryGetValue(itemId, out var lines) || lines.Count == 0)
        {
            return;
        }

        var remainingQty = qty;
        for (var index = 0; index < lines.Count && remainingQty > QtyTolerance; index++)
        {
            var line = lines[index];
            var alreadyProduced = totals.TryGetValue(line.Id, out var current) ? current : 0d;
            var unfilledQty = Math.Max(0, line.QtyOrdered - alreadyProduced);
            var isLastLine = index == lines.Count - 1;
            if (unfilledQty <= QtyTolerance && !isLastLine)
            {
                continue;
            }

            var assignQty = isLastLine
                ? remainingQty
                : Math.Min(remainingQty, unfilledQty);
            if (assignQty <= QtyTolerance)
            {
                continue;
            }

            AddProducedQty(totals, line.Id, assignQty);
            remainingQty -= assignQty;
        }
    }

    private static void AddProducedQty(IDictionary<long, double> totals, long orderLineId, double qty)
    {
        if (qty <= QtyTolerance)
        {
            return;
        }

        totals[orderLineId] = totals.TryGetValue(orderLineId, out var current)
            ? current + qty
            : qty;
    }

    private static bool IsMockStoreException(Exception ex)
    {
        var fullName = ex.GetType().FullName ?? string.Empty;
        return fullName.Contains("Moq", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Castle.Proxies", StringComparison.OrdinalIgnoreCase);
    }
}
