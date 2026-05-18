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

        if (customerLines.Count == 0)
        {
            return result;
        }

        var internalOrders = store.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Id != targetCustomerOrderId
                            && order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled))
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToList();

        if (internalOrders.Count == 0)
        {
            return result;
        }

        var redistribution = new OrderRedistributionService(store);
        var transfers = new List<OrderAutoRedistributionTransfer>();

        foreach (var customerLine in customerLines)
        {
            var remainingCap = customerLine.QtyOrdered;
            foreach (var internalOrder in internalOrders)
            {
                if (remainingCap <= QtyTolerance)
                {
                    break;
                }

                var internalLine = store.GetOrderLines(internalOrder.Id)
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

                var customerQtyBefore = store.GetOrderLines(targetCustomerOrderId)
                    .Where(line => line.Id == customerLine.Id)
                    .Select(line => line.QtyOrdered)
                    .FirstOrDefault();

                if (customerQtyBefore <= QtyTolerance)
                {
                    break;
                }

                var preDecrementQty = Math.Min(transferQty, customerQtyBefore);
                store.UpdateOrderLineQty(customerLine.Id, customerQtyBefore - preDecrementQty);

                try
                {
                    var redistributeResult = redistribution.Redistribute(
                        internalOrder.Id,
                        targetCustomerOrderId,
                        customerLine.ItemId,
                        preDecrementQty);

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
                    remainingCap -= preDecrementQty;
                }
                catch (InvalidOperationException ex)
                {
                    store.UpdateOrderLineQty(customerLine.Id, customerQtyBefore);
                    result.IgnoredAttempts.Add(new OrderAutoRedistributionIgnoredAttempt
                    {
                        SourceOrderId = internalOrder.Id,
                        SourceOrderRef = internalOrder.OrderRef,
                        ItemId = customerLine.ItemId,
                        Qty = preDecrementQty,
                        Reason = ex.Message
                    });
                }
                catch (ArgumentException ex)
                {
                    store.UpdateOrderLineQty(customerLine.Id, customerQtyBefore);
                    result.IgnoredAttempts.Add(new OrderAutoRedistributionIgnoredAttempt
                    {
                        SourceOrderId = internalOrder.Id,
                        SourceOrderRef = internalOrder.OrderRef,
                        ItemId = customerLine.ItemId,
                        Qty = preDecrementQty,
                        Reason = ex.Message
                    });
                }
            }
        }

        result.Transfers.AddRange(transfers);
        return result;
    }
}

public sealed class OrderAutoRedistributionApplyResult
{
    public long TargetOrderId { get; init; }
    public string? SkippedReason { get; set; }
    public List<OrderAutoRedistributionTransfer> Transfers { get; } = new();
    public List<OrderAutoRedistributionIgnoredAttempt> IgnoredAttempts { get; } = new();

    public bool HasTransfers => Transfers.Count > 0;
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
}
