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
        store.Setup(data => data.GetOrdersPage(true, null, 4, 0))
            .Returns(new[]
            {
                CreateOrder(11, "INT-002", OrderStatus.InProgress, OrderType.Internal, dueDate: new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc)),
                CreateOrder(10, "CUST-001", OrderStatus.InProgress, OrderType.Customer, dueDate: new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc)),
                CreateOrder(13, "CUST-010", OrderStatus.Accepted, OrderType.Customer, dueDate: new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc)),
                CreateOrder(12, "CUST-003", OrderStatus.Shipped, OrderType.Customer, dueDate: new DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc))
            });
        SetupStableStatus(store, orderId: 10, qtyOrdered: 10, shippedQty: 0, receivedQty: 0, expectedStatus: null);
        SetupStableStatus(store, orderId: 11, qtyOrdered: 10, shippedQty: 0, receivedQty: 0, expectedStatus: null);
        SetupStableStatus(store, orderId: 13, qtyOrdered: 10, shippedQty: 0, receivedQty: 10, expectedStatus: null);
        SetupStableStatus(store, orderId: 12, qtyOrdered: 10, shippedQty: 10, receivedQty: 0, expectedStatus: null);
        store.Setup(data => data.GetOrderShippedAt(12))
            .Returns(new DateTime(2026, 5, 16, 10, 0, 0, DateTimeKind.Utc));

        var result = new OrderService(store.Object).GetOrdersPage(true, null, 4, 0);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("INT-002", first.OrderRef);
                Assert.Equal(OrderStatus.InProgress, first.Status);
            },
            second =>
            {
                Assert.Equal("CUST-001", second.OrderRef);
                Assert.Equal(OrderType.Customer, second.Type);
                Assert.Equal(OrderStatus.InProgress, second.Status);
            },
            third =>
            {
                Assert.Equal("CUST-010", third.OrderRef);
                Assert.Equal(OrderStatus.Accepted, third.Status);
            },
            fourth =>
            {
                Assert.Equal("CUST-003", fourth.OrderRef);
                Assert.Equal(OrderStatus.Shipped, fourth.Status);
            });
        store.Verify(data => data.GetOrdersPage(true, null, 4, 0), Times.Once);
        store.Verify(data => data.GetOrders(), Times.Never);
    }

    [Fact]
    public void GetOrdersPage_FastPathForWebPaging_UsesPagedStoreCallWithoutLegacyStatusReads()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderReadModelStore>();
        store.Setup(data => data.GetOrdersPage(true, null, 21, 0))
            .Returns(new[]
            {
                CreateOrder(10, "CUST-010", OrderStatus.InProgress, OrderType.Customer),
                CreateOrder(11, "INT-011", OrderStatus.InProgress, OrderType.Internal)
            });

        var result = new OrderService(store.Object).GetOrdersPage(true, null, 21, 0);

        Assert.Equal(2, result.Count);
        Assert.Collection(
            result,
            first => Assert.Equal("CUST-010", first.OrderRef),
            second =>
            {
                Assert.Equal("INT-011", second.OrderRef);
                Assert.Equal(OrderType.Internal, second.Type);
                Assert.Equal(OrderStatus.InProgress, second.Status);
            });
        store.Verify(data => data.GetOrdersPage(true, null, 21, 0), Times.Once);
        store.Verify(data => data.GetOrders(), Times.Never);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetOrdersPage_FastPathPreservesEffectiveStatusesForPagedSorting()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderReadModelStore>();
        store.Setup(data => data.GetOrdersPage(true, null, 21, 0))
            .Returns(new[]
            {
                CreateOrder(56, "056", OrderStatus.InProgress, OrderType.Internal),
                CreateOrder(53, "053", OrderStatus.Accepted, OrderType.Customer),
                CreateOrder(30, "030", OrderStatus.Draft, OrderType.Customer),
                CreateOrder(45, "045", OrderStatus.Shipped, OrderType.Internal)
            });

        var result = new OrderService(store.Object).GetOrdersPage(true, null, 21, 0);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("056", first.OrderRef);
                Assert.Equal(OrderStatus.InProgress, first.Status);
                Assert.Equal(OrderType.Internal, first.Type);
            },
            second =>
            {
                Assert.Equal("053", second.OrderRef);
                Assert.Equal(OrderStatus.Accepted, second.Status);
            },
            third =>
            {
                Assert.Equal("030", third.OrderRef);
                Assert.Equal(OrderStatus.Draft, third.Status);
                Assert.Equal(OrderType.Customer, third.Type);
            },
            fourth =>
            {
                Assert.Equal("045", fourth.OrderRef);
                Assert.Equal(OrderStatus.Shipped, fourth.Status);
            });
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
