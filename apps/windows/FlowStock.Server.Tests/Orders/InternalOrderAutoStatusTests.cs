using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class InternalOrderAutoStatusTests
{
    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.InProgress)]
    public void RefreshPersistedStatus_WhenQtyOrderedZeroAndNoProduction_DoesNotBecomeShipped(OrderStatus initialStatus)
    {
        const long orderId = 66;
        const long lineId = 6601;

        var store = CreateStore(orderId, initialStatus, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 0 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 0,
                QtyReceived = 0,
                QtyRemaining = 0
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.NotEqual(OrderStatus.Shipped, status);
        Assert.Equal(initialStatus == OrderStatus.Draft ? OrderStatus.Draft : OrderStatus.InProgress, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Never);
    }

    [Fact]
    public void RefreshPersistedStatus_WhenQtyOrderedPositiveAndNoProduction_StaysInProgress()
    {
        const long orderId = 67;
        const long lineId = 6701;

        var store = CreateStore(orderId, OrderStatus.InProgress, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 3600 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 3600,
                QtyReceived = 0,
                QtyRemaining = 3600
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.Equal(OrderStatus.InProgress, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Never);
    }

    [Fact]
    public void RefreshPersistedStatus_WhenFullyCoveredByClosedProduction_BecomesShipped()
    {
        const long orderId = 68;
        const long lineId = 6801;

        var store = CreateStore(orderId, OrderStatus.InProgress, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 3600 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 3600,
                QtyReceived = 3600,
                QtyRemaining = 0
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.Equal(OrderStatus.Shipped, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Once);
    }

    private static Mock<IDataStore> CreateStore(
        long orderId,
        OrderStatus status,
        IReadOnlyList<OrderLine> lines,
        IReadOnlyList<OrderReceiptLine> receiptRemaining)
    {
        var order = new Order
        {
            Id = orderId,
            OrderRef = $"INT-{orderId}",
            Type = OrderType.Internal,
            Status = status,
            CreatedAt = new DateTime(2026, 1, 1)
        };

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId)).Returns(order);
        store.Setup(s => s.GetOrderLines(orderId)).Returns(lines);
        store.Setup(s => s.GetOrderReceiptRemaining(orderId)).Returns(receiptRemaining);
        store.Setup(s => s.UpdateOrderStatus(orderId, It.IsAny<OrderStatus>()));
        return store;
    }
}
