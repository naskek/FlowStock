using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderAutoRedistributionService
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _data;

    public OrderAutoRedistributionService(IDataStore data)
    {
        _data = data;
    }

    public OrderAutoRedistributionApplyResult ApplyFromOpenInternalOrders(long targetCustomerOrderId)
    {
        OrderAutoRedistributionApplyResult? result = null;
        _data.ExecuteInTransaction(store =>
        {
            result = ApplyFromOpenInternalOrdersCore(store, targetCustomerOrderId);
        });

        return result ?? new OrderAutoRedistributionApplyResult();
    }

    private static OrderAutoRedistributionApplyResult ApplyFromOpenInternalOrdersCore(
        IDataStore store,
        long targetCustomerOrderId)
    {
        var result = new OrderAutoRedistributionApplyResult { TargetOrderId = targetCustomerOrderId };
        var targetOrder = store.GetOrder(targetCustomerOrderId);
        if (targetOrder == null)
        {
            result.SkippedReason = "ORDER_NOT_FOUND";
            return result;
        }

        if (targetOrder.Type != OrderType.Customer)
        {
            result.SkippedReason = "TARGET_NOT_CUSTOMER";
            return result;
        }

        if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            result.SkippedReason = "ORDER_NOT_EDITABLE";
            return result;
        }

        if (!targetOrder.UseReservedStock)
        {
            result.SkippedReason = "CUSTOMER_RESERVATION_DISABLED";
            return result;
        }

        var customerLines = store.GetOrderLines(targetCustomerOrderId)
            .Where(line => line.QtyOrdered > QtyTolerance)
            .GroupBy(line => line.ItemId)
            .Select(group => group.OrderBy(line => line.Id).First())
            .ToList();
        result.CustomerLineCount = customerLines.Count;

        if (customerLines.Count == 0)
        {
            result.SkippedReason = "TARGET_CUSTOMER_HAS_NO_OPEN_LINES";
            return result;
        }

        result.InternalStatusRefresh = OrderService.RefreshInternalOrderStatuses(store);
        if (result.InternalStatusRefresh.ChangedCount > 0)
        {
            result.Warnings.Add(new OrderAutoRedistributionWarning
            {
                Code = "INTERNAL_STATUSES_REFRESHED",
                Message = $"Перед автопереносом обновлены статусы INTERNAL: {result.InternalStatusRefresh.ChangedCount}."
            });
        }

        var internalOrders = store.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Id != targetCustomerOrderId
                            && order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged))
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToList();
        result.OpenInternalCandidateCount = internalOrders.Count;

        if (internalOrders.Count == 0)
        {
            result.SkippedReason = "NO_OPEN_INTERNAL_ORDERS";
            return result;
        }

        var customerItemIds = customerLines.Select(line => line.ItemId).ToHashSet();
        var internalLinesByOrder = internalOrders.ToDictionary(
            order => order.Id,
            order => store.GetOrderLines(order.Id).ToList());
        var matchingCandidates = internalOrders
            .SelectMany(order => internalLinesByOrder[order.Id].Select(line => new { Order = order, Line = line }))
            .Where(row => customerItemIds.Contains(row.Line.ItemId))
            .ToList();
        result.MatchingInternalCandidateCount = matchingCandidates.Count;
        if (matchingCandidates.Count == 0)
        {
            result.SkippedReason = "OPEN_INTERNAL_WITHOUT_MATCHING_ITEM";
            return result;
        }

        var matchingQtyZeroCandidates = matchingCandidates
            .Where(row => row.Line.QtyOrdered <= QtyTolerance)
            .ToList();
        foreach (var candidate in matchingQtyZeroCandidates)
        {
            if (InternalOrderMergeService.HasActiveProductionPalletPlan(store, candidate.Order.Id))
            {
                result.Warnings.Add(new OrderAutoRedistributionWarning
                {
                    Code = InternalOrderMergeService.ActivePalletPlanWarningCode,
                    SourceOrderId = candidate.Order.Id,
                    SourceOrderRef = candidate.Order.OrderRef,
                    ItemId = candidate.Line.ItemId,
                    Message = "Внутренний заказ уже без количества, но у него остался активный план паллет. Сначала удалите или перенесите план паллет."
                });
            }
        }

        var redistribution = new OrderRedistributionService(store);
        var transfers = new List<OrderAutoRedistributionTransfer>();
        var sourceMergeResults = new Dictionary<long, InternalOrderMergeResult>();

        foreach (var customerLine in customerLines)
        {
            var remainingCap = customerLine.QtyOrdered;
            foreach (var internalOrder in internalOrders)
            {
                if (remainingCap <= QtyTolerance)
                {
                    break;
                }

                var internalLine = internalLinesByOrder[internalOrder.Id]
                    .Where(line => line.ItemId == customerLine.ItemId && line.QtyOrdered > QtyTolerance)
                    .OrderBy(line => line.Id)
                    .FirstOrDefault();

                if (internalLine == null)
                {
                    continue;
                }

                var transferQty = Math.Min(remainingCap, internalLine.QtyOrdered);
                if (transferQty <= QtyTolerance)
                {
                    continue;
                }

                var producedByLine = OrderReceiptRemainingCalculator.BuildProducedTotalsByOrderLine(
                    store,
                    internalOrder.Id,
                    new[] { internalLine });
                var producedQty = producedByLine.TryGetValue(internalLine.Id, out var producedValue) ? producedValue : 0d;
                var bindableProducedQty = targetOrder.UseReservedStock
                    ? OrderRedistributionService.GetBindableStockQtyFromSource(
                        store,
                        internalOrder.Id,
                        targetCustomerOrderId,
                        customerLine.ItemId)
                    : 0d;
                var unproducedRemaining = Math.Max(0, internalLine.QtyOrdered - producedQty);
                var redistributionGuard = InternalOrderRedistributionGuard.Evaluate(store, internalOrder.Id);
                if (redistributionGuard.IsBlocked)
                {
                    transferQty = Math.Min(transferQty, bindableProducedQty);
                    if (transferQty <= QtyTolerance)
                    {
                        result.IgnoredAttempts.Add(new OrderAutoRedistributionIgnoredAttempt
                        {
                            SourceOrderId = internalOrder.Id,
                            SourceOrderRef = internalOrder.OrderRef,
                            ItemId = customerLine.ItemId,
                            Qty = Math.Min(remainingCap, internalLine.QtyOrdered),
                            Reason = InternalOrderRedistributionGuardResult.BlockedMessage,
                            Guard = redistributionGuard
                        });
                        continue;
                    }
                }
                else
                {
                    transferQty = Math.Min(transferQty, bindableProducedQty + unproducedRemaining);
                }

                if (transferQty <= QtyTolerance)
                {
                    continue;
                }

                var customerQtyBefore = store.GetOrderLines(targetCustomerOrderId)
                    .Where(line => line.Id == customerLine.Id)
                    .Select(line => line.QtyOrdered)
                    .FirstOrDefault();
                double qtyFromUnproduced = 0;
                try
                {
                    var split = OrderRedistributionService.ComputeTransferQuantities(
                        store,
                        internalOrder,
                        targetOrder,
                        internalOrder.Id,
                        targetCustomerOrderId,
                        customerLine.ItemId,
                        transferQty);
                    qtyFromUnproduced = split.QtyFromUnproduced;
                    if (qtyFromUnproduced > QtyTolerance && customerQtyBefore > QtyTolerance)
                    {
                        store.UpdateOrderLineQty(
                            customerLine.Id,
                            customerQtyBefore - Math.Min(qtyFromUnproduced, customerQtyBefore));
                    }

                    var redistributeResult = redistribution.Redistribute(
                        internalOrder.Id,
                        targetCustomerOrderId,
                        customerLine.ItemId,
                        transferQty);

                    transfers.Add(new OrderAutoRedistributionTransfer
                    {
                        SourceOrderId = redistributeResult.SourceOrderId,
                        SourceOrderRef = internalOrder.OrderRef,
                        TargetOrderId = redistributeResult.TargetOrderId,
                        ItemId = redistributeResult.ItemId,
                        QtyTransferred = redistributeResult.QtyTransferred,
                        QtyFromUnproduced = redistributeResult.QtyFromUnproduced,
                        QtyFromProducedStock = redistributeResult.QtyFromProducedStock,
                        TransferredHuCodes = redistributeResult.TransferredHuCodes
                    });
                    if (redistributeResult.SourceMergeResult is { } mergeResult
                        && (!sourceMergeResults.TryGetValue(redistributeResult.SourceOrderId, out var existing)
                            || (!existing.IsMerged && mergeResult.IsMerged)
                            || (mergeResult.IsMerged && !string.IsNullOrWhiteSpace(mergeResult.InfoMessage))))
                    {
                        sourceMergeResults[redistributeResult.SourceOrderId] = mergeResult;
                    }

                    remainingCap -= redistributeResult.QtyTransferred;
                }
                catch (InvalidOperationException ex)
                {
                    if (qtyFromUnproduced > QtyTolerance && customerQtyBefore > QtyTolerance)
                    {
                        store.UpdateOrderLineQty(customerLine.Id, customerQtyBefore);
                    }

                    var guard = string.Equals(
                        ex.Message,
                        InternalOrderRedistributionGuardResult.BlockedMessage,
                        StringComparison.Ordinal)
                        ? InternalOrderRedistributionGuard.Evaluate(store, internalOrder.Id)
                        : null;
                    result.IgnoredAttempts.Add(new OrderAutoRedistributionIgnoredAttempt
                    {
                        SourceOrderId = internalOrder.Id,
                        SourceOrderRef = internalOrder.OrderRef,
                        ItemId = customerLine.ItemId,
                        Qty = transferQty,
                        Reason = ex.Message,
                        Guard = guard
                    });
                }
                catch (ArgumentException ex)
                {
                    if (qtyFromUnproduced > QtyTolerance && customerQtyBefore > QtyTolerance)
                    {
                        store.UpdateOrderLineQty(customerLine.Id, customerQtyBefore);
                    }

                    result.IgnoredAttempts.Add(new OrderAutoRedistributionIgnoredAttempt
                    {
                        SourceOrderId = internalOrder.Id,
                        SourceOrderRef = internalOrder.OrderRef,
                        ItemId = customerLine.ItemId,
                        Qty = transferQty,
                        Reason = ex.Message
                    });
                }
            }
        }

        result.Transfers.AddRange(transfers);
        foreach (var sourceOrderId in transfers.Select(transfer => transfer.SourceOrderId).Distinct())
        {
            var sourceOrder = store.GetOrder(sourceOrderId);
            if (sourceOrder == null)
            {
                continue;
            }

            var mergeResult = sourceMergeResults.TryGetValue(sourceOrderId, out var mergeFromTransfer)
                ? mergeFromTransfer
                : InternalOrderMergeService.TryMarkAsMerged(
                    store,
                    sourceOrderId,
                    targetCustomerOrderId,
                    targetOrder.OrderRef);
            if (mergeResult.IsMerged && !string.IsNullOrWhiteSpace(mergeResult.InfoMessage))
            {
                result.Warnings.Add(new OrderAutoRedistributionWarning
                {
                    Code = mergeResult.InfoCode ?? InternalOrderMergeService.MergedInfoCode,
                    SourceOrderId = sourceOrderId,
                    SourceOrderRef = sourceOrder.OrderRef,
                    Message = mergeResult.InfoMessage
                });
            }
            else if (!string.IsNullOrWhiteSpace(mergeResult.WarningCode))
            {
                result.Warnings.Add(new OrderAutoRedistributionWarning
                {
                    Code = mergeResult.WarningCode,
                    SourceOrderId = sourceOrderId,
                    SourceOrderRef = sourceOrder.OrderRef,
                    Message = mergeResult.WarningMessage ?? string.Empty
                });
            }
        }

        if (!result.HasTransfers && string.IsNullOrWhiteSpace(result.SkippedReason))
        {
            result.SkippedReason = matchingQtyZeroCandidates.Count > 0
                ? result.Warnings.Any(warning => warning.Code == InternalOrderMergeService.ActivePalletPlanWarningCode)
                    ? InternalOrderMergeService.ActivePalletPlanWarningCode
                    : "OPEN_INTERNAL_MATCHING_ITEM_QTY_ZERO"
                : "OPEN_INTERNAL_WITHOUT_MATCHING_ITEM";
        }

        return result;
    }
}

public sealed class OrderAutoRedistributionApplyResult
{
    public long TargetOrderId { get; init; }
    public string? SkippedReason { get; set; }
    public int CustomerLineCount { get; set; }
    public int OpenInternalCandidateCount { get; set; }
    public int MatchingInternalCandidateCount { get; set; }
    public OrderStatusRefreshReport InternalStatusRefresh { get; set; } = new();
    public List<OrderAutoRedistributionTransfer> Transfers { get; } = new();
    public List<OrderAutoRedistributionIgnoredAttempt> IgnoredAttempts { get; } = new();
    public List<OrderAutoRedistributionWarning> Warnings { get; } = new();

    public bool HasTransfers => Transfers.Count > 0;
}

public sealed class OrderAutoRedistributionWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public long? SourceOrderId { get; init; }
    public string? SourceOrderRef { get; init; }
    public long? ItemId { get; init; }
}

public sealed class OrderAutoRedistributionTransfer
{
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public long TargetOrderId { get; init; }
    public long ItemId { get; init; }
    public double QtyTransferred { get; init; }
    public double QtyFromUnproduced { get; init; }
    public double QtyFromProducedStock { get; init; }
    public IReadOnlyList<string> TransferredHuCodes { get; init; } = Array.Empty<string>();
}

public sealed class OrderAutoRedistributionIgnoredAttempt
{
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public double Qty { get; init; }
    public string Reason { get; init; } = string.Empty;
    public InternalOrderRedistributionGuardResult? Guard { get; init; }
}
