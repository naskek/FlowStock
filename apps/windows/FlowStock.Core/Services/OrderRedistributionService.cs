using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderRedistributionService
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _data;

    public OrderRedistributionService(IDataStore data)
    {
        _data = data;
    }

    public OrderRedistributionResult Redistribute(
        long sourceInternalOrderId,
        long targetCustomerOrderId,
        long itemId,
        double qty)
    {
        if (qty <= QtyTolerance)
        {
            throw new ArgumentException("Количество переноса должно быть больше нуля.", nameof(qty));
        }

        OrderRedistributionResult? result = null;
        _data.ExecuteInTransaction(store =>
        {
            result = RedistributeCore(store, sourceInternalOrderId, targetCustomerOrderId, itemId, qty);
        });

        return result ?? throw new InvalidOperationException("Перераспределение не выполнено.");
    }

    private static OrderRedistributionResult RedistributeCore(
        IDataStore store,
        long sourceInternalOrderId,
        long targetCustomerOrderId,
        long itemId,
        double qty)
    {
        var sourceOrder = store.GetOrder(sourceInternalOrderId)
                          ?? throw new InvalidOperationException("Внутренний заказ-источник не найден.");
        var targetOrder = store.GetOrder(targetCustomerOrderId)
                          ?? throw new InvalidOperationException("Клиентский заказ-получатель не найден.");

        if (sourceOrder.Type != OrderType.Internal)
        {
            throw new InvalidOperationException("Источник перераспределения должен быть внутренним заказом.");
        }

        if (targetOrder.Type != OrderType.Customer)
        {
            throw new InvalidOperationException("Получатель перераспределения должен быть клиентским заказом.");
        }

        if (sourceOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException("Внутренний заказ-источник недоступен для перераспределения.");
        }

        if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException("Клиентский заказ-получатель недоступен для перераспределения.");
        }

        if (sourceInternalOrderId == targetCustomerOrderId)
        {
            throw new InvalidOperationException("Нельзя перераспределить позицию в тот же заказ.");
        }

        var sourceLine = store.GetOrderLines(sourceInternalOrderId)
            .Where(line => line.ItemId == itemId && line.QtyOrdered > QtyTolerance)
            .OrderBy(line => line.Id)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Позиция не найдена во внутреннем заказе.");

        var producedByLine = OrderReceiptRemainingCalculator.BuildProducedTotalsByOrderLine(
            store,
            sourceInternalOrderId,
            new[] { sourceLine });
        var produced = producedByLine.TryGetValue(sourceLine.Id, out var producedQty) ? producedQty : 0d;
        var maxTransfer = sourceLine.QtyOrdered;
        var transferQty = Math.Min(qty, maxTransfer);
        if (transferQty <= QtyTolerance)
        {
            throw new InvalidOperationException("Нет доступного объема для переноса по внутреннему заказу.");
        }

        var unproducedRemaining = Math.Max(0, sourceLine.QtyOrdered - produced);
        var qtyFromUnproduced = Math.Min(transferQty, unproducedRemaining);
        var qtyFromProduced = transferQty - qtyFromUnproduced;
        if (qtyFromProduced > QtyTolerance)
        {
            var bindableQty = GetBindableStockQtyFromSource(store, sourceInternalOrderId, targetCustomerOrderId, itemId);
            if (bindableQty + QtyTolerance < qtyFromProduced)
            {
                throw new InvalidOperationException(
                    "Недостаточно выпущенного товара на складе по внутреннему заказу для переноса с привязкой HU. " +
                    $"Доступно: {bindableQty:0.###}, требуется: {qtyFromProduced:0.###}.");
            }

            if (!targetOrder.UseReservedStock)
            {
                throw new InvalidOperationException(
                    "Для переноса выпущенного объема с привязкой HU у клиентского заказа должен быть включен резерв складского остатка.");
            }

            if (!ItemTypeUsesOrderReservation(store, itemId))
            {
                throw new InvalidOperationException(
                    "Тип номенклатуры не поддерживает резервирование HU под клиентский заказ.");
            }
        }

        var newSourceQty = sourceLine.QtyOrdered - transferQty;
        if (newSourceQty + QtyTolerance < produced)
        {
            throw new InvalidOperationException(
                "Нельзя перенести больше, чем незакрытый остаток внутреннего заказа с учетом уже выпущенного объема.");
        }

        store.UpdateOrderLineQty(sourceLine.Id, newSourceQty);

        var targetLines = store.GetOrderLines(targetCustomerOrderId)
            .Where(line => line.ItemId == itemId)
            .OrderBy(line => line.Id)
            .ToList();
        double targetQtyAfter;
        if (targetLines.Count > 0)
        {
            var targetLine = targetLines[0];
            targetQtyAfter = targetLine.QtyOrdered + transferQty;
            store.UpdateOrderLineQty(targetLine.Id, targetQtyAfter);
            for (var i = 1; i < targetLines.Count; i++)
            {
                store.DeleteOrderLine(targetLines[i].Id);
            }
        }
        else
        {
            var newLineId = store.AddOrderLine(new OrderLine
            {
                OrderId = targetCustomerOrderId,
                ItemId = itemId,
                QtyOrdered = transferQty,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
            targetQtyAfter = transferQty;
            _ = newLineId;
        }

        var orderService = new OrderService(store);
        orderService.TryRebuildOrderReceiptPlan(store, sourceInternalOrderId);
        if (targetOrder.UseReservedStock)
        {
            OrderService.RefreshCustomerReceiptPlansCore(store);
        }

        return new OrderRedistributionResult
        {
            SourceOrderId = sourceInternalOrderId,
            TargetOrderId = targetCustomerOrderId,
            ItemId = itemId,
            QtyTransferred = transferQty,
            QtyFromUnproduced = qtyFromUnproduced,
            QtyFromProducedStock = qtyFromProduced,
            SourceQtyOrderedAfter = newSourceQty,
            TargetQtyOrderedAfter = targetQtyAfter
        };
    }

    private static double GetBindableStockQtyFromSource(
        IDataStore store,
        long sourceInternalOrderId,
        long targetCustomerOrderId,
        long itemId)
    {
        var sourceHuCodes = CollectSourceOrderHuCodes(store, sourceInternalOrderId, itemId);
        if (sourceHuCodes.Count == 0)
        {
            return 0;
        }

        var stockByHu = store.GetHuStockRows()
            .Where(row => row.ItemId == itemId && row.Qty > QtyTolerance)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.Sum(entry => entry.Qty), StringComparer.OrdinalIgnoreCase);

        var contextByHu = store.GetHuOrderContextRows()
            .Where(row => row.ItemId == itemId)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var total = 0d;
        foreach (var huCode in sourceHuCodes)
        {
            if (!stockByHu.TryGetValue(huCode, out var stockQty) || stockQty <= QtyTolerance)
            {
                continue;
            }

            if (contextByHu.TryGetValue(huCode, out var context)
                && context.ReservedCustomerOrderId.HasValue
                && context.ReservedCustomerOrderId.Value != targetCustomerOrderId)
            {
                continue;
            }

            if (context?.OriginInternalOrderId is long originId
                && originId != sourceInternalOrderId
                && !sourceHuCodes.Contains(huCode))
            {
                continue;
            }

            total += stockQty;
        }

        return total;
    }

    private static HashSet<string> CollectSourceOrderHuCodes(IDataStore store, long sourceInternalOrderId, long itemId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundHu = new OrderService(store).GetOrderBoundHuByItem(sourceInternalOrderId);
        if (boundHu.TryGetValue(itemId, out var bound))
        {
            foreach (var hu in bound)
            {
                result.Add(hu);
            }
        }

        foreach (var doc in store.GetDocsByOrder(sourceInternalOrderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => pallet.ItemId == itemId
                                            && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                var hu = NormalizeHu(pallet.HuCode);
                if (!string.IsNullOrWhiteSpace(hu))
                {
                    result.Add(hu);
                }
            }
        }

        foreach (var row in store.GetHuOrderContextRows()
                     .Where(row => row.ItemId == itemId && row.OriginInternalOrderId == sourceInternalOrderId))
        {
            var hu = NormalizeHu(row.HuCode);
            if (!string.IsNullOrWhiteSpace(hu))
            {
                result.Add(hu);
            }
        }

        return result;
    }

    private static bool ItemTypeUsesOrderReservation(IDataStore store, long itemId)
    {
        var item = store.FindItemById(itemId);
        if (item?.ItemTypeId is not long itemTypeId)
        {
            return false;
        }

        return store.GetItemType(itemTypeId)?.EnableOrderReservation == true;
    }

    private static string NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}

public sealed class OrderRedistributionResult
{
    public long SourceOrderId { get; init; }
    public long TargetOrderId { get; init; }
    public long ItemId { get; init; }
    public double QtyTransferred { get; init; }
    public double QtyFromUnproduced { get; init; }
    public double QtyFromProducedStock { get; init; }
    public double SourceQtyOrderedAfter { get; init; }
    public double TargetQtyOrderedAfter { get; init; }
}
