using LightWms.Core.Abstractions;
using LightWms.Core.Models;
using System.Linq;

namespace LightWms.Core.Services;

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
        ApplyLineMetrics(orderId, lines);
        return lines;
    }

    public IReadOnlyDictionary<long, double> GetItemAvailability()
    {
        return _data.GetLedgerTotalsByItem();
    }

    public IReadOnlyDictionary<long, double> GetShippedTotals(long orderId)
    {
        return _data.GetShippedTotalsByOrder(orderId);
    }

    public long CreateOrder(string orderRef, long partnerId, DateTime? dueDate, OrderStatus status, string? comment, IReadOnlyList<OrderLineView> lines)
    {
        if (string.IsNullOrWhiteSpace(orderRef))
        {
            throw new ArgumentException("Номер заказа обязателен.", nameof(orderRef));
        }

        if (_data.GetPartner(partnerId) == null)
        {
            throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
        }

        if (status == OrderStatus.Shipped)
        {
            throw new ArgumentException("Статус \"Отгружен\" ставится автоматически.", nameof(status));
        }

        var order = new Order
        {
            OrderRef = orderRef.Trim(),
            PartnerId = partnerId,
            DueDate = dueDate?.Date,
            Status = status,
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

    public void UpdateOrder(long orderId, string orderRef, long partnerId, DateTime? dueDate, OrderStatus status, string? comment, IReadOnlyList<OrderLineView> lines)
    {
        var existing = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (existing.Status == OrderStatus.Shipped)
        {
            throw new InvalidOperationException("Отгруженный заказ нельзя редактировать.");
        }

        if (_data.GetPartner(partnerId) == null)
        {
            throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
        }

        if (status == OrderStatus.Shipped)
        {
            throw new ArgumentException("Статус \"Отгружен\" ставится автоматически.", nameof(status));
        }

        if (string.IsNullOrWhiteSpace(orderRef))
        {
            throw new ArgumentException("Номер заказа обязателен.", nameof(orderRef));
        }

        var updated = new Order
        {
            Id = orderId,
            OrderRef = orderRef.Trim(),
            PartnerId = partnerId,
            DueDate = dueDate?.Date,
            Status = status,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = existing.CreatedAt
        };

        var normalized = NormalizeLines(lines);

        _data.ExecuteInTransaction(store =>
        {
            store.UpdateOrder(updated);
            store.DeleteOrderLines(orderId);
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
    }

    private void ApplyLineMetrics(long orderId, IReadOnlyList<OrderLineView> lines)
    {
        var availableByItem = _data.GetLedgerTotalsByItem();
        var shippedByItem = _data.GetShippedTotalsByOrder(orderId);

        foreach (var line in lines)
        {
            var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
            var shipped = shippedByItem.TryGetValue(line.ItemId, out var shippedQty) ? shippedQty : 0;
            var remaining = Math.Max(0, line.QtyOrdered - shipped);
            var availableForShip = Math.Max(0, available);
            var canShip = Math.Min(remaining, availableForShip);
            var shortage = Math.Max(0, remaining - availableForShip);

            line.QtyAvailable = available;
            line.QtyShipped = shipped;
            line.QtyRemaining = remaining;
            line.CanShipNow = canShip;
            line.Shortage = shortage;
        }
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
        var hasOutbound = _data.HasOutboundDocs(order.Id);
        var lines = _data.GetOrderLines(order.Id);
        var shippedTotals = _data.GetShippedTotalsByOrder(order.Id);

        var fullyShipped = lines.Count > 0 && lines.All(line =>
        {
            var shipped = shippedTotals.TryGetValue(line.ItemId, out var qty) ? qty : 0;
            return shipped + QtyTolerance >= line.QtyOrdered;
        });

        var nextStatus = order.Status;
        if (fullyShipped)
        {
            nextStatus = OrderStatus.Shipped;
        }
        else if (hasOutbound && order.Status == OrderStatus.Accepted)
        {
            nextStatus = OrderStatus.InProgress;
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
