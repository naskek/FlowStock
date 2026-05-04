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

    private static Order CreateOrder(long id, string orderRef, OrderStatus status)
    {
        return new Order
        {
            Id = id,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)
        };
    }
}
