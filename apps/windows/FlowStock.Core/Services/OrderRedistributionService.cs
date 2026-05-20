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

        if (sourceOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
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

        var redistributionGuard = InternalOrderRedistributionGuard.Evaluate(store, sourceInternalOrderId);
        if (redistributionGuard.IsBlocked)
        {
            throw new InvalidOperationException(InternalOrderRedistributionGuardResult.BlockedMessage);
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

        var activeSourcePallets = newSourceQty <= QtyTolerance
            ? CollectActiveSourcePallets(store, sourceInternalOrderId, sourceLine.Id, itemId)
            : Array.Empty<SourceProductionPallet>();
        if (activeSourcePallets.Any(row => row.DocStatus == DocStatus.Closed
                                          || row.DocLedgerCount > 0
                                          || string.Equals(row.Pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "План паллет не соответствует строкам заказа. Запустите диагностику production-plan-consistency.");
        }

        var sourcePlanBefore = store.GetOrderReceiptPlanLines(sourceInternalOrderId).ToList();
        var transferredPlanSegments = PeelSourceReceiptPlan(
            sourcePlanBefore,
            sourceLine.Id,
            itemId,
            transferQty,
            out var remainingSourcePlan);

        store.UpdateOrderLineQty(sourceLine.Id, newSourceQty);

        long targetLineId;
        double targetQtyAfter;
        var targetLines = store.GetOrderLines(targetCustomerOrderId)
            .Where(line => line.ItemId == itemId)
            .OrderBy(line => line.Id)
            .ToList();
        if (targetLines.Count > 0)
        {
            var targetLine = targetLines[0];
            targetLineId = targetLine.Id;
            targetQtyAfter = targetLine.QtyOrdered + transferQty;
            store.UpdateOrderLineQty(targetLine.Id, targetQtyAfter);
            for (var i = 1; i < targetLines.Count; i++)
            {
                store.DeleteOrderLine(targetLines[i].Id);
            }
        }
        else
        {
            targetLineId = store.AddOrderLine(new OrderLine
            {
                OrderId = targetCustomerOrderId,
                ItemId = itemId,
                QtyOrdered = transferQty,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
            targetQtyAfter = transferQty;
        }

        var normalizedRemainingPlan = NormalizeInternalPlanAfterTransfer(
            remainingSourcePlan,
            sourceLine.Id,
            itemId,
            newSourceQty);
        store.ReplaceOrderReceiptPlanLines(sourceInternalOrderId, normalizedRemainingPlan);

        var remainingPlanQty = normalizedRemainingPlan
            .Where(line => line.OrderLineId == sourceLine.Id && line.ItemId == itemId)
            .Sum(line => line.QtyPlanned);
        if (newSourceQty > QtyTolerance && remainingPlanQty + QtyTolerance < newSourceQty)
        {
            var orderService = new OrderService(store);
            orderService.TryRebuildOrderReceiptPlan(store, sourceInternalOrderId);
        }

        var transferredHuCodes = transferredPlanSegments
            .Select(segment => segment.HuCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (newSourceQty <= QtyTolerance && activeSourcePallets.Count > 0)
        {
            transferredHuCodes = transferredHuCodes
                .Concat(activeSourcePallets.Select(row => NormalizeHu(row.Pallet.HuCode)))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (targetOrder.UseReservedStock && ItemTypeUsesOrderReservation(store, itemId))
        {
            ApplyTransferredPlanToCustomer(
                store,
                targetCustomerOrderId,
                targetLineId,
                itemId,
                transferredPlanSegments);

            if (qtyFromProduced > QtyTolerance)
            {
                AppendStockReservationFromSource(
                    store,
                    sourceInternalOrderId,
                    targetCustomerOrderId,
                    targetLineId,
                    itemId,
                    qtyFromProduced);
            }
        }

        if (transferredHuCodes.Count > 0)
        {
            store.ReassignOpenProductionPalletsByHu(
                sourceInternalOrderId,
                targetCustomerOrderId,
                targetLineId,
                itemId,
                transferredHuCodes);
        }

        OrderService.RefreshCustomerReceiptPlansCore(store, preserveOrderId: targetCustomerOrderId);
        var mergeResult = InternalOrderMergeService.TryMarkAsMerged(
            store,
            sourceInternalOrderId,
            targetCustomerOrderId,
            targetOrder.OrderRef);
        EmptyDraftProductionReceiptCleanup.CleanupEmptyDraftProductionReceiptsForOrder(store, sourceInternalOrderId);

        return new OrderRedistributionResult
        {
            SourceOrderId = sourceInternalOrderId,
            TargetOrderId = targetCustomerOrderId,
            ItemId = itemId,
            QtyTransferred = transferQty,
            QtyFromUnproduced = qtyFromUnproduced,
            QtyFromProducedStock = qtyFromProduced,
            SourceQtyOrderedAfter = newSourceQty,
            TargetQtyOrderedAfter = targetQtyAfter,
            TransferredHuCodes = transferredHuCodes,
            SourceMergeResult = mergeResult
        };
    }

    private static IReadOnlyList<SourceProductionPallet> CollectActiveSourcePallets(
        IDataStore store,
        long sourceInternalOrderId,
        long sourceOrderLineId,
        long itemId)
    {
        var result = new List<SourceProductionPallet>();
        foreach (var doc in store.GetDocsByOrder(sourceInternalOrderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                         .Where(pallet => pallet.ItemId == itemId || pallet.Lines.Any(line => line.ItemId == itemId))
                         .Where(pallet => pallet.OrderLineId == sourceOrderLineId || pallet.Lines.Any(line => line.OrderLineId == sourceOrderLineId)))
            {
                result.Add(new SourceProductionPallet(
                    pallet,
                    doc.Status,
                    store.CountLedgerEntriesByDocId(doc.Id)));
            }
        }

        return result;
    }

    private static List<OrderReceiptPlanLine> NormalizeInternalPlanAfterTransfer(
        IReadOnlyList<OrderReceiptPlanLine> remainingPlan,
        long orderLineId,
        long itemId,
        double newSourceQty)
    {
        if (newSourceQty <= QtyTolerance)
        {
            return remainingPlan
                .Where(line => line.OrderLineId != orderLineId || line.ItemId != itemId)
                .Select(line => ClonePlanLine(line))
                .ToList();
        }

        var otherLines = remainingPlan
            .Where(line => line.OrderLineId != orderLineId || line.ItemId != itemId)
            .Select(line => ClonePlanLine(line))
            .ToList();
        var linePlan = remainingPlan
            .Where(line => line.OrderLineId == orderLineId && line.ItemId == itemId)
            .OrderBy(line => line.SortOrder)
            .ToList();
        var linePlanQty = linePlan.Sum(line => line.QtyPlanned);
        if (linePlanQty <= newSourceQty + QtyTolerance)
        {
            otherLines.AddRange(linePlan.Select(line => ClonePlanLine(line)));
            return otherLines;
        }

        var excessPlanQty = linePlanQty - newSourceQty;
        var trimmedLinePlan = new List<OrderReceiptPlanLine>();
        foreach (var line in linePlan)
        {
            if (excessPlanQty <= QtyTolerance)
            {
                trimmedLinePlan.Add(ClonePlanLine(line));
                continue;
            }

            var removeQty = Math.Min(excessPlanQty, line.QtyPlanned);
            var keepQty = line.QtyPlanned - removeQty;
            excessPlanQty -= removeQty;
            if (keepQty > QtyTolerance)
            {
                trimmedLinePlan.Add(ClonePlanLine(line, keepQty));
            }
        }

        otherLines.AddRange(trimmedLinePlan);
        return otherLines;
    }

    private static List<PlanTransferSegment> PeelSourceReceiptPlan(
        IReadOnlyList<OrderReceiptPlanLine> sourcePlan,
        long orderLineId,
        long itemId,
        double qtyToTransfer,
        out List<OrderReceiptPlanLine> remainingPlan)
    {
        remainingPlan = new List<OrderReceiptPlanLine>();
        var transferred = new List<PlanTransferSegment>();
        var remainingQty = qtyToTransfer;

        foreach (var line in sourcePlan.OrderBy(line => line.SortOrder))
        {
            if (line.OrderLineId != orderLineId || line.ItemId != itemId)
            {
                remainingPlan.Add(ClonePlanLine(line));
                continue;
            }

            if (remainingQty <= QtyTolerance)
            {
                remainingPlan.Add(ClonePlanLine(line));
                continue;
            }

            var takeQty = Math.Min(remainingQty, line.QtyPlanned);
            if (takeQty > QtyTolerance)
            {
                transferred.Add(new PlanTransferSegment(
                    NormalizeHuOrNull(line.ToHu),
                    line.ToLocationId,
                    takeQty));
                remainingQty -= takeQty;
            }

            var leftQty = line.QtyPlanned - takeQty;
            if (leftQty > QtyTolerance)
            {
                remainingPlan.Add(ClonePlanLine(line, leftQty));
            }
        }

        return transferred;
    }

    private static void ApplyTransferredPlanToCustomer(
        IDataStore store,
        long targetOrderId,
        long targetOrderLineId,
        long itemId,
        IReadOnlyList<PlanTransferSegment> transferredSegments)
    {
        if (transferredSegments.Count == 0)
        {
            return;
        }

        var customerPlan = store.GetOrderReceiptPlanLines(targetOrderId).ToList();
        var nextSortOrder = customerPlan.Count == 0
            ? 0
            : customerPlan.Max(line => line.SortOrder) + 1;
        foreach (var segment in transferredSegments)
        {
            customerPlan.Add(new OrderReceiptPlanLine
            {
                OrderId = targetOrderId,
                OrderLineId = targetOrderLineId,
                ItemId = itemId,
                QtyPlanned = segment.Qty,
                ToLocationId = segment.LocationId,
                ToHu = segment.HuCode,
                SortOrder = nextSortOrder++
            });
        }

        store.ReplaceOrderReceiptPlanLines(targetOrderId, customerPlan);
    }

    private static void AppendStockReservationFromSource(
        IDataStore store,
        long sourceInternalOrderId,
        long targetCustomerOrderId,
        long targetOrderLineId,
        long itemId,
        double qty)
    {
        var stockByHu = store.GetHuStockRows()
            .Where(row => row.ItemId == itemId && row.Qty > QtyTolerance)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.Sum(entry => entry.Qty), StringComparer.OrdinalIgnoreCase);
        var sourceHuCodes = CollectSourceOrderHuCodes(store, sourceInternalOrderId, itemId);
        var contextByHu = store.GetHuOrderContextRows()
            .Where(row => row.ItemId == itemId)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var customerPlan = store.GetOrderReceiptPlanLines(targetCustomerOrderId).ToList();
        var nextSortOrder = customerPlan.Count == 0
            ? 0
            : customerPlan.Max(line => line.SortOrder) + 1;
        var remaining = qty;
        foreach (var huCode in sourceHuCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            if (remaining <= QtyTolerance)
            {
                break;
            }

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

            if (customerPlan.Any(line => string.Equals(NormalizeHu(line.ToHu), huCode, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var takeQty = Math.Min(remaining, stockQty);
            var locationId = customerPlan.FirstOrDefault(line => line.ToLocationId.HasValue)?.ToLocationId
                             ?? store.GetLocations().FirstOrDefault()?.Id;
            customerPlan.Add(new OrderReceiptPlanLine
            {
                OrderId = targetCustomerOrderId,
                OrderLineId = targetOrderLineId,
                ItemId = itemId,
                QtyPlanned = takeQty,
                ToLocationId = locationId,
                ToHu = huCode,
                SortOrder = nextSortOrder++
            });
            remaining -= takeQty;
        }

        if (remaining > QtyTolerance)
        {
            throw new InvalidOperationException(
                "Недостаточно выпущенного товара на складе по внутреннему заказу для переноса с привязкой HU. " +
                $"Осталось зарезервировать: {remaining:0.###}.");
        }

        store.ReplaceOrderReceiptPlanLines(targetCustomerOrderId, customerPlan);
    }

    private static OrderReceiptPlanLine ClonePlanLine(OrderReceiptPlanLine line, double? qtyPlanned = null)
    {
        return new OrderReceiptPlanLine
        {
            OrderId = line.OrderId,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            QtyPlanned = qtyPlanned ?? line.QtyPlanned,
            ToLocationId = line.ToLocationId,
            ToHu = line.ToHu,
            SortOrder = line.SortOrder
        };
    }

    private static string? NormalizeHuOrNull(string? value)
    {
        var normalized = NormalizeHu(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record PlanTransferSegment(string? HuCode, long? LocationId, double Qty);

    private sealed record SourceProductionPallet(ProductionPallet Pallet, DocStatus DocStatus, int DocLedgerCount);

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
    public IReadOnlyList<string> TransferredHuCodes { get; init; } = Array.Empty<string>();
    public InternalOrderMergeResult? SourceMergeResult { get; init; }
}
