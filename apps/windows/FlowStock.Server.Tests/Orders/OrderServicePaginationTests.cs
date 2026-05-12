using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderServicePaginationTests
{
    [Fact]
    public void GetOrdersPage_ForwardsLimitOffsetSearchAndIncludeInternalToDataStore()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(data => data.GetOrdersPage(true, "002", 20, 40))
            .Returns(new[]
            {
                CreateOrder(1, "002", OrderStatus.Cancelled)
            });

        var result = new OrderService(store.Object).GetOrdersPage(true, "002", 20, 40);

        var order = Assert.Single(result);
        Assert.Equal("002", order.OrderRef);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        store.Verify(data => data.GetOrdersPage(true, "002", 20, 40), Times.Once);
        store.Verify(data => data.GetOrders(), Times.Never);
    }

    [Fact]
    public void GetOrdersPage_PreservesReturnedPageShape()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(data => data.GetOrdersPage(false, null, 2, 1))
            .Returns(new[]
            {
                CreateOrder(2, "002", OrderStatus.Cancelled),
                CreateOrder(3, "003", OrderStatus.Cancelled)
            });

        var result = new OrderService(store.Object).GetOrdersPage(false, null, 2, 1);

        Assert.Equal(2, result.Count);
        Assert.Collection(
            result,
            first => Assert.Equal("002", first.OrderRef),
            second => Assert.Equal("003", second.OrderRef));
        store.Verify(data => data.GetOrdersPage(false, null, 2, 1), Times.Once);
        store.Verify(data => data.GetOrders(), Times.Never);
    }

    [Fact]
    public void GetOrdersPage_PreservesCanonicalServerOrder_ForMixedTypesAndStatuses()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(data => data.GetOrdersPage(true, null, 3, 0))
            .Returns(new[]
            {
                CreateOrder(10, "CUST-001", OrderStatus.InProgress, OrderType.Customer, dueDate: new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc)),
                CreateOrder(11, "INT-002", OrderStatus.InProgress, OrderType.Internal, dueDate: new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc)),
                CreateOrder(12, "CUST-003", OrderStatus.Shipped, OrderType.Customer, dueDate: new DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc))
            });
        SetupStableStatus(store, orderId: 10, qtyOrdered: 10, shippedQty: 0, receivedQty: 0, expectedStatus: null);
        SetupStableStatus(store, orderId: 11, qtyOrdered: 10, shippedQty: 0, receivedQty: 0, expectedStatus: null);
        SetupStableStatus(store, orderId: 12, qtyOrdered: 10, shippedQty: 10, receivedQty: 0, expectedStatus: null);
        store.Setup(data => data.GetOrderShippedAt(12))
            .Returns(new DateTime(2026, 5, 16, 10, 0, 0, DateTimeKind.Utc));

        var result = new OrderService(store.Object).GetOrdersPage(true, null, 3, 0);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("CUST-001", first.OrderRef);
                Assert.Equal(OrderStatus.InProgress, first.Status);
            },
            second =>
            {
                Assert.Equal("INT-002", second.OrderRef);
                Assert.Equal(OrderType.Internal, second.Type);
                Assert.Equal(OrderStatus.InProgress, second.Status);
            },
            third =>
            {
                Assert.Equal("CUST-003", third.OrderRef);
                Assert.Equal(OrderStatus.Shipped, third.Status);
            });
        store.Verify(data => data.GetOrdersPage(true, null, 3, 0), Times.Once);
        store.Verify(data => data.GetOrders(), Times.Never);
    }

    private static void SetupStableStatus(
        Mock<IDataStore> store,
        long orderId,
        double qtyOrdered,
        double shippedQty,
        double receivedQty,
        OrderStatus? expectedStatus)
    {
        store.Setup(data => data.GetOrderLines(orderId))
            .Returns(new[]
            {
                new OrderLine
                {
                    Id = orderId * 100,
                    OrderId = orderId,
                    ItemId = orderId * 10,
                    QtyOrdered = qtyOrdered
                }
            });
        store.Setup(data => data.GetShippedTotalsByOrderLine(orderId))
            .Returns(new Dictionary<long, double> { [orderId * 100] = shippedQty });
        store.Setup(data => data.GetOrderReceiptRemaining(orderId))
            .Returns(new[]
            {
                new OrderReceiptLine
                {
                    OrderLineId = orderId * 100,
                    OrderId = orderId,
                    ItemId = orderId * 10,
                    QtyOrdered = qtyOrdered,
                    QtyReceived = receivedQty,
                    QtyRemaining = Math.Max(0, qtyOrdered - receivedQty)
                }
            });
        if (expectedStatus.HasValue)
        {
            store.Setup(data => data.UpdateOrderStatus(orderId, expectedStatus.Value));
        }
    }

    private static Order CreateOrder(
        long id,
        string orderRef,
        OrderStatus status,
        OrderType type = OrderType.Customer,
        DateTime? dueDate = null)
    {
        return new Order
        {
            Id = id,
            OrderRef = orderRef,
            Type = type,
            DueDate = dueDate,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)
        };
    }
}
