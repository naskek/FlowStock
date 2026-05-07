using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionNeedOrderCreationService(IDataStore dataStore)
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _dataStore = dataStore;

    public ProductionNeedOrderCreationResult CreateDraftOrders()
    {
        var customerDraftSpecs = BuildCustomerDraftSpecs();
        var internalDraftLines = BuildInternalDraftLines();
        if (customerDraftSpecs.Count == 0 && internalDraftLines.Count == 0)
        {
            return new ProductionNeedOrderCreationResult
            {
                Message = "Новой потребности для формирования нет."
            };
        }

        var orderService = new OrderService(_dataStore);
        var customerOrderIds = new List<long>();
        long? internalOrderId = null;
        var createdLineCount = 0;

        foreach (var spec in customerDraftSpecs)
        {
            var orderId = orderService.CreateDraftOrder(
                GenerateNextOrderRef(),
                spec.PartnerId,
                spec.DueDate,
                "Автосформировано из потребности производства.",
                spec.Lines,
                OrderType.Customer);
            customerOrderIds.Add(orderId);
            createdLineCount += spec.Lines.Count;
        }

        if (internalDraftLines.Count > 0)
        {
            internalOrderId = orderService.CreateDraftOrder(
                GenerateNextOrderRef(),
                null,
                null,
                "Автосформировано из потребности производства.",
                internalDraftLines,
                OrderType.Internal);
            createdLineCount += internalDraftLines.Count;
        }

        return new ProductionNeedOrderCreationResult
        {
            CustomerDraftCount = customerOrderIds.Count,
            InternalDraftCount = internalOrderId.HasValue ? 1 : 0,
            CreatedLineCount = createdLineCount,
            CustomerDraftOrderIds = customerOrderIds,
            InternalDraftOrderId = internalOrderId,
            Message = BuildMessage(customerOrderIds.Count, internalOrderId.HasValue ? 1 : 0, createdLineCount)
        };
    }

    private List<CustomerDraftSpec> BuildCustomerDraftSpecs()
    {
        var activeDemandOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status is not OrderStatus.Draft and not OrderStatus.Shipped and not OrderStatus.Cancelled)
            .ToList();
        if (activeDemandOrders.Count == 0)
        {
            return [];
        }

        var plannedByPartnerAndItem = BuildExistingCustomerDraftQtyByPartnerAndItem();
        var needByPartner = new Dictionary<long, Dictionary<long, CustomerNeedLine>>();

        foreach (var order in activeDemandOrders)
        {
            if (!order.PartnerId.HasValue)
            {
                continue;
            }

            var shippedQtyByLine = _dataStore.GetShippedTotalsByOrderLine(order.Id);
            var reservedQtyByLine = _dataStore.GetOrderReceiptPlanLines(order.Id)
                .GroupBy(line => line.OrderLineId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.QtyPlanned));

            foreach (var line in _dataStore.GetOrderLines(order.Id))
            {
                var shippedQty = shippedQtyByLine.TryGetValue(line.Id, out var shippedValue) ? shippedValue : 0;
                var reservedQty = reservedQtyByLine.TryGetValue(line.Id, out var reservedValue)
                    ? Math.Max(0, reservedValue)
                    : 0;
                var outstandingQty = Math.Max(0, line.QtyOrdered - shippedQty - reservedQty);
                if (outstandingQty <= QtyTolerance)
                {
                    continue;
                }

                if (!needByPartner.TryGetValue(order.PartnerId.Value, out var linesByItem))
                {
                    linesByItem = new Dictionary<long, CustomerNeedLine>();
                    needByPartner[order.PartnerId.Value] = linesByItem;
                }

                if (linesByItem.TryGetValue(line.ItemId, out var existing))
                {
                    linesByItem[line.ItemId] = existing with
                    {
                        Qty = existing.Qty + outstandingQty,
                        DueDate = MinDate(existing.DueDate, order.DueDate)
                    };
                }
                else
                {
                    linesByItem[line.ItemId] = new CustomerNeedLine(outstandingQty, order.DueDate);
                }
            }
        }

        var result = new List<CustomerDraftSpec>();
        foreach (var partnerEntry in needByPartner.OrderBy(entry => entry.Key))
        {
            var lines = new List<OrderLineView>();
            DateTime? dueDate = null;

            foreach (var itemEntry in partnerEntry.Value.OrderBy(entry => entry.Key))
            {
                var itemId = itemEntry.Key;
                var requiredQty = itemEntry.Value.Qty;
                if (plannedByPartnerAndItem.TryGetValue((partnerEntry.Key, itemId), out var plannedQty))
                {
                    requiredQty = Math.Max(0, requiredQty - plannedQty);
                }

                if (requiredQty <= QtyTolerance)
                {
                    continue;
                }

                var item = _dataStore.FindItemById(itemId) ?? throw new InvalidOperationException("Товар потребности не найден.");
                lines.Add(new OrderLineView
                {
                    ItemId = itemId,
                    ItemName = item.Name,
                    QtyOrdered = requiredQty,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                });
                dueDate = MinDate(dueDate, itemEntry.Value.DueDate);
            }

            if (lines.Count == 0)
            {
                continue;
            }

            result.Add(new CustomerDraftSpec(partnerEntry.Key, dueDate, lines));
        }

        return result;
    }

    private Dictionary<(long PartnerId, long ItemId), double> BuildExistingCustomerDraftQtyByPartnerAndItem()
    {
        var result = new Dictionary<(long PartnerId, long ItemId), double>();
        var existingDrafts = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status == OrderStatus.Draft
                            && order.PartnerId.HasValue)
            .ToList();

        foreach (var order in existingDrafts)
        {
            foreach (var line in GetRemainingPlannedLines(order))
            {
                if (line.QtyRemaining <= QtyTolerance || !order.PartnerId.HasValue)
                {
                    continue;
                }

                var key = (order.PartnerId.Value, line.ItemId);
                result[key] = result.TryGetValue(key, out var current)
                    ? current + line.QtyRemaining
                    : line.QtyRemaining;
            }
        }

        return result;
    }

    private List<OrderLineView> BuildInternalDraftLines()
    {
        return new ProductionNeedService(_dataStore)
            .GetRows(includeZeroNeed: false)
            .Where(row => row.ToMinStockQty > QtyTolerance)
            .Select(row =>
            {
                var item = _dataStore.FindItemById(row.ItemId) ?? throw new InvalidOperationException("Товар потребности не найден.");
                return new OrderLineView
                {
                    ItemId = row.ItemId,
                    ItemName = item.Name,
                    QtyOrdered = row.ToMinStockQty,
                    ProductionPurpose = ProductionLinePurpose.InternalStock
                };
            })
            .ToList();
    }

    private List<PlannedLine> GetRemainingPlannedLines(Order order)
    {
        var receiptRemainingByLine = _dataStore.GetOrderReceiptRemaining(order.Id)
            .ToDictionary(line => line.OrderLineId);

        return _dataStore.GetOrderLines(order.Id)
            .Select(line =>
            {
                if (receiptRemainingByLine.TryGetValue(line.Id, out var receiptLine))
                {
                    return new PlannedLine(line.ItemId, Math.Max(0, receiptLine.QtyRemaining), receiptLine.ProductionPurpose);
                }

                return new PlannedLine(line.ItemId, Math.Max(0, line.QtyOrdered), line.ProductionPurpose);
            })
            .Where(line => line.QtyRemaining > QtyTolerance)
            .ToList();
    }

    private string GenerateNextOrderRef()
    {
        long max = 0;
        foreach (var order in _dataStore.GetOrders())
        {
            var orderRef = order.OrderRef?.Trim();
            if (string.IsNullOrWhiteSpace(orderRef))
            {
                continue;
            }

            if (!orderRef.All(char.IsDigit))
            {
                continue;
            }

            if (long.TryParse(orderRef, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value > max)
            {
                max = value;
            }
        }

        return (max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    private static DateTime? MinDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
        {
            return right;
        }

        if (!right.HasValue)
        {
            return left;
        }

        return left.Value <= right.Value ? left : right;
    }

    private static string BuildMessage(int customerDraftCount, int internalDraftCount, int createdLineCount)
    {
        return customerDraftCount == 0 && internalDraftCount == 0
            ? "Новой потребности для формирования нет."
            : $"Созданы черновики: клиентские: {customerDraftCount}, внутренние: {internalDraftCount}, строк: {createdLineCount}.";
    }

    private sealed record CustomerDraftSpec(long PartnerId, DateTime? DueDate, IReadOnlyList<OrderLineView> Lines);
    private sealed record CustomerNeedLine(double Qty, DateTime? DueDate);
    private sealed record PlannedLine(long ItemId, double QtyRemaining, ProductionLinePurpose ProductionPurpose);
}
