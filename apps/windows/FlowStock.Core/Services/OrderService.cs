using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using System.Linq;

namespace FlowStock.Core.Services;

public sealed class OrderService
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _data;

    public OrderService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<Order> GetOrders()
    {
        var orders = _data.GetOrders();
        var result = new List<Order>(orders.Count);
        foreach (var order in orders)
        {
            result.Add(ApplyAutoStatus(order));
        }

        return result;
    }

    public Order? GetOrder(long id)
    {
        var order = _data.GetOrder(id);
        return order == null ? null : ApplyAutoStatus(order);
    }

    public IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId)
    {
        var order = _data.GetOrder(orderId);
        if (order == null)
        {
            return Array.Empty<OrderLineView>();
        }

        var lines = _data.GetOrderLineViews(orderId);
        ApplyLineMetrics(order, lines);
        return lines;
    }

    public IReadOnlyDictionary<long, double> GetItemAvailability()
    {
        return _data.GetLedgerTotalsByItem();
    }

    public IReadOnlyDictionary<long, double> GetShippedTotals(long orderId)
    {
        return _data.GetShippedTotalsByOrderLine(orderId);
    }

    public long CreateOrder(string orderRef, long? partnerId, DateTime? dueDate, string? comment, IReadOnlyList<OrderLineView> lines, OrderType type = OrderType.Customer)
    {
        if (string.IsNullOrWhiteSpace(orderRef))
        {
            throw new ArgumentException("Номер заказа обязателен.", nameof(orderRef));
        }

        if (type == OrderType.Customer)
        {
            if (!partnerId.HasValue)
            {
                throw new ArgumentException("Контрагент обязателен.", nameof(partnerId));
            }

            if (_data.GetPartner(partnerId.Value) == null)
            {
                throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
            }
        }

        var order = new Order
        {
            OrderRef = orderRef.Trim(),
            Type = type,
            PartnerId = type == OrderType.Customer ? partnerId : null,
            DueDate = dueDate?.Date,
            Status = OrderStatus.InProgress,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = DateTime.Now
        };

        var normalized = NormalizeLines(lines);
        long orderId = 0;

        _data.ExecuteInTransaction(store =>
        {
            orderId = store.AddOrder(order);
            foreach (var line in normalized)
            {
                store.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                });
            }
        });

        return orderId;
    }

    public void UpdateOrder(long orderId, string orderRef, long? partnerId, DateTime? dueDate, string? comment, IReadOnlyList<OrderLineView> lines, OrderType type = OrderType.Customer)
    {
        var existing = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (existing.Status == OrderStatus.Shipped)
        {
            throw new InvalidOperationException($"{OrderStatusMapper.StatusToDisplayName(OrderStatus.Shipped, existing.Type)} заказ нельзя редактировать.");
        }

        if (existing.Type != type)
        {
            if ((existing.Type == OrderType.Customer && type == OrderType.Internal))
            {
                if (_data.HasOutboundDocs(orderId))
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: есть отгрузки или связанные документы.");
                }

                var shippedTotals = _data.GetShippedTotalsByOrderLine(orderId);
                if (shippedTotals.Values.Any(qty => qty > QtyTolerance))
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: по заказу уже есть отгрузки.");
                }
            }
            else if (existing.Type == OrderType.Internal && type == OrderType.Customer)
            {
                var hasProductionReceipts = _data.GetDocsByOrder(orderId)
                    .Any(doc => doc.Type == DocType.ProductionReceipt);
                if (hasProductionReceipts)
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: по внутреннему заказу уже есть выпуски продукции.");
                }

                var receiptRemaining = _data.GetOrderReceiptRemaining(orderId);
                if (receiptRemaining.Any(line => line.QtyReceived > QtyTolerance))
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: по внутреннему заказу уже есть выпуски продукции.");
                }
            }
            else
            {
                throw new InvalidOperationException("Смена типа заказа разрешена только между клиентским и внутренним заказом.");
            }
        }

        if (type == OrderType.Customer)
        {
            if (!partnerId.HasValue)
            {
                throw new ArgumentException("Контрагент обязателен.", nameof(partnerId));
            }

            if (_data.GetPartner(partnerId.Value) == null)
            {
                throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
            }
        }

        if (string.IsNullOrWhiteSpace(orderRef))
        {
            throw new ArgumentException("Номер заказа обязателен.", nameof(orderRef));
        }

        var updated = new Order
        {
            Id = orderId,
            OrderRef = orderRef.Trim(),
            Type = type,
            PartnerId = type == OrderType.Customer ? partnerId : null,
            DueDate = dueDate?.Date,
            Status = existing.Status == OrderStatus.Shipped ? OrderStatus.Shipped : OrderStatus.InProgress,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = existing.CreatedAt
        };

        var normalized = NormalizeLines(lines);

        _data.ExecuteInTransaction(store =>
        {
            store.UpdateOrder(updated);

            var existingLines = store.GetOrderLines(orderId);
            var existingByItem = existingLines
                .GroupBy(line => line.ItemId)
                .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToList());
            var incomingItemIds = normalized.Select(line => line.ItemId).ToHashSet();

            foreach (var line in normalized)
            {
                if (existingByItem.TryGetValue(line.ItemId, out var matched) && matched.Count > 0)
                {
                    var primary = matched[0];
                    if (Math.Abs(primary.QtyOrdered - line.QtyOrdered) > QtyTolerance)
                    {
                        store.UpdateOrderLineQty(primary.Id, line.QtyOrdered);
                    }

                    // Legacy cleanup: keep one line per item, remove accidental duplicates.
                    for (var i = 1; i < matched.Count; i++)
                    {
                        store.DeleteOrderLine(matched[i].Id);
                    }
                    continue;
                }

                store.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                });
            }

            foreach (var entry in existingByItem)
            {
                if (incomingItemIds.Contains(entry.Key))
                {
                    continue;
                }

                foreach (var staleLine in entry.Value)
                {
                    store.DeleteOrderLine(staleLine.Id);
                }
            }
        });
    }

    public void DeleteOrder(long orderId)
    {
        var existing = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (existing.Status != OrderStatus.Draft)
        {
            throw new InvalidOperationException("Удалить можно только заказ в статусе \"Черновик\".");
        }

        if (_data.HasOutboundDocs(orderId))
        {
            throw new InvalidOperationException("Нельзя удалить заказ: есть отгрузки или связанные документы.");
        }

        var shippedTotals = _data.GetShippedTotalsByOrderLine(orderId);
        if (shippedTotals.Values.Any(qty => qty > QtyTolerance))
        {
            throw new InvalidOperationException("Нельзя удалить заказ: есть отгрузки.");
        }

        if (existing.Type == OrderType.Internal)
        {
            var hasProductionReceipts = _data.GetDocsByOrder(orderId)
                .Any(doc => doc.Type == DocType.ProductionReceipt);
            if (hasProductionReceipts)
            {
                throw new InvalidOperationException("Нельзя удалить внутренний заказ: есть выпуски продукции или связанные документы.");
            }

            var receiptRemaining = _data.GetOrderReceiptRemaining(orderId);
            if (receiptRemaining.Any(line => line.QtyReceived > QtyTolerance))
            {
                throw new InvalidOperationException("Нельзя удалить внутренний заказ: по нему уже был выпуск продукции.");
            }
        }

        _data.ExecuteInTransaction(store =>
        {
            store.DeleteOrderLines(orderId);
            store.DeleteOrder(orderId);
        });
    }

    public void ChangeOrderStatus(long orderId, OrderStatus status)
    {
        throw new InvalidOperationException("Ручное изменение статуса заказа отключено. Статус определяется автоматически по выпуску и отгрузке.");
    }

    private void ApplyLineMetrics(Order order, IReadOnlyList<OrderLineView> lines)
    {
        var availableByItem = _data.GetLedgerTotalsByItem();
        if (order.Type == OrderType.Internal)
        {
            var producedByLine = _data.GetOrderReceiptRemaining(order.Id)
                .ToDictionary(line => line.OrderLineId, line => line.QtyReceived);

            foreach (var line in lines)
            {
                var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
                var produced = producedByLine.TryGetValue(line.Id, out var producedQty) ? producedQty : 0;
                var remaining = Math.Max(0, line.QtyOrdered - produced);

                line.QtyAvailable = available;
                line.QtyProduced = produced;
                line.QtyShipped = produced;
                line.QtyRemaining = remaining;
                line.CanShipNow = 0;
                line.Shortage = 0;
            }

            return;
        }

        var shippedByLine = _data.GetShippedTotalsByOrderLine(order.Id);
        var producedByOrderLine = _data.GetOrderReceiptRemaining(order.Id)
            .ToDictionary(line => line.OrderLineId, line => line.QtyReceived);
        var reservedOutstandingByItem = GetReservedOutstandingByItemForCustomerOrders();

        foreach (var line in lines)
        {
            var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
            var shipped = shippedByLine.TryGetValue(line.Id, out var shippedQty) ? shippedQty : 0;
            var produced = producedByOrderLine.TryGetValue(line.Id, out var producedQty) ? producedQty : 0;
            var remaining = Math.Max(0, line.QtyOrdered - shipped);
            var reservedForLine = Math.Max(0, produced - shipped);
            var reservedByItem = reservedOutstandingByItem.TryGetValue(line.ItemId, out var reservedQty)
                ? reservedQty
                : 0;
            var unreservedAvailable = Math.Max(0, Math.Max(0, available) - reservedByItem);
            var availableForShip = reservedForLine + unreservedAvailable;
            var canShip = Math.Min(remaining, availableForShip);
            var shortage = Math.Max(0, remaining - availableForShip);

            line.QtyAvailable = available;
            line.QtyShipped = shipped;
            line.QtyProduced = produced;
            line.QtyRemaining = remaining;
            line.CanShipNow = canShip;
            line.Shortage = shortage;
        }
    }

    private IReadOnlyDictionary<long, double> GetReservedOutstandingByItemForCustomerOrders()
    {
        var reserved = new Dictionary<long, double>();
        var customerOrders = _data.GetOrders()
            .Where(order => order.Type == OrderType.Customer)
            .ToList();

        foreach (var customerOrder in customerOrders)
        {
            var shippedByLine = _data.GetShippedTotalsByOrderLine(customerOrder.Id);
            foreach (var receiptLine in _data.GetOrderReceiptRemaining(customerOrder.Id))
            {
                var shipped = shippedByLine.TryGetValue(receiptLine.OrderLineId, out var shippedQty)
                    ? shippedQty
                    : 0;
                var outstanding = Math.Max(0, receiptLine.QtyReceived - shipped);
                if (outstanding <= QtyTolerance)
                {
                    continue;
                }

                reserved[receiptLine.ItemId] = reserved.TryGetValue(receiptLine.ItemId, out var current)
                    ? current + outstanding
                    : outstanding;
            }
        }

        return reserved;
    }

    private static List<OrderLineView> NormalizeLines(IReadOnlyList<OrderLineView> lines)
    {
        var grouped = new Dictionary<long, OrderLineView>();
        foreach (var line in lines)
        {
            if (line.QtyOrdered <= 0)
            {
                continue;
            }

            if (grouped.TryGetValue(line.ItemId, out var existing))
            {
                existing.QtyOrdered += line.QtyOrdered;
                continue;
            }

            grouped[line.ItemId] = new OrderLineView
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QtyOrdered = line.QtyOrdered
            };
        }

        return grouped.Values.ToList();
    }

    private Order ApplyAutoStatus(Order order)
    {
        if (order.Type == OrderType.Internal)
        {
            var receiptLines = _data.GetOrderReceiptRemaining(order.Id);
            var fullyProduced = receiptLines.Count > 0 && receiptLines.All(line => line.QtyReceived + QtyTolerance >= line.QtyOrdered);
            var anyProduced = receiptLines.Any(line => line.QtyReceived > QtyTolerance);

            var internalStatus = order.Status;
            if (fullyProduced)
            {
                internalStatus = OrderStatus.Shipped;
            }
            else if (anyProduced)
            {
                internalStatus = OrderStatus.InProgress;
            }
            else
            {
                internalStatus = OrderStatus.InProgress;
            }

            if (internalStatus != order.Status)
            {
                _data.UpdateOrderStatus(order.Id, internalStatus);
            }

            var completedAt = internalStatus == OrderStatus.Shipped
                ? _data.GetDocsByOrder(order.Id)
                    .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed && doc.ClosedAt.HasValue)
                    .Select(doc => doc.ClosedAt!.Value)
                    .DefaultIfEmpty()
                    .Max()
                : (DateTime?)null;

            return new Order
            {
                Id = order.Id,
                OrderRef = order.OrderRef,
                Type = order.Type,
                PartnerId = order.PartnerId,
                DueDate = order.DueDate,
                Status = internalStatus,
                Comment = order.Comment,
                CreatedAt = order.CreatedAt,
                ShippedAt = completedAt == DateTime.MinValue ? null : completedAt,
                PartnerName = order.PartnerName,
                PartnerCode = order.PartnerCode
            };
        }

        var lines = _data.GetOrderLines(order.Id);
        var shippedTotals = _data.GetShippedTotalsByOrderLine(order.Id);
        var hasShipped = shippedTotals.Values.Any(qty => qty > QtyTolerance);
        var customerReceiptLines = _data.GetOrderReceiptRemaining(order.Id);
        var producedByLine = customerReceiptLines.ToDictionary(line => line.OrderLineId, line => line.QtyReceived);
        var hasProduced = customerReceiptLines.Any(line => line.QtyReceived > QtyTolerance);

        var fullyShipped = lines.Count > 0 && lines.All(line =>
        {
            var shipped = shippedTotals.TryGetValue(line.Id, out var qty) ? qty : 0;
            return shipped + QtyTolerance >= line.QtyOrdered;
        });

        var nextStatus = OrderStatus.InProgress;
        if (fullyShipped)
        {
            nextStatus = OrderStatus.Shipped;
        }
        else
        {
            var fullyProducedForOrder = lines.Count > 0 && lines.All(line =>
            {
                var produced = producedByLine.TryGetValue(line.Id, out var qty) ? qty : 0;
                return produced + QtyTolerance >= line.QtyOrdered;
            });
            if (hasProduced || fullyProducedForOrder)
            {
                nextStatus = OrderStatus.Accepted;
            }

            if (hasShipped && !fullyShipped)
            {
                nextStatus = OrderStatus.InProgress;
            }
        }

        if (nextStatus != order.Status)
        {
            _data.UpdateOrderStatus(order.Id, nextStatus);
        }

        var shippedAt = fullyShipped ? _data.GetOrderShippedAt(order.Id) : null;
        return new Order
        {
            Id = order.Id,
            OrderRef = order.OrderRef,
            Type = order.Type,
            PartnerId = order.PartnerId,
            DueDate = order.DueDate,
            Status = nextStatus,
            Comment = order.Comment,
            CreatedAt = order.CreatedAt,
            ShippedAt = shippedAt,
            PartnerName = order.PartnerName,
            PartnerCode = order.PartnerCode
        };
    }
}

