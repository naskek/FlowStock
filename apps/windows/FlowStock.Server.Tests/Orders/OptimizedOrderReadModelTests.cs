using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OptimizedOrderReadModelTests
{
    [Fact]
    public void GetOrderReceiptRemaining_ForInternalOrder_PreservesFallbackDistributionWithOptimizedUnlinkedTotals()
    {
        const long orderId = 50;
        const long itemId = 1001;

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId)).Returns(new Order
        {
            Id = orderId,
            OrderRef = "INT-050",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc)
        });
        store.Setup(s => s.GetOrderLines(orderId)).Returns(
        [
            new OrderLine
            {
                Id = 501,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = 5
            },
            new OrderLine
            {
                Id = 502,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = 5
            }
        ]);
        store.Setup(s => s.GetOrderReceiptRemaining(orderId)).Returns(
        [
            new OrderReceiptLine
            {
                OrderLineId = 501,
                OrderId = orderId,
                ItemId = itemId,
                ItemName = "Товар",
                QtyOrdered = 5,
                QtyReceived = 4,
                QtyRemaining = 1
            },
            new OrderReceiptLine
            {
                OrderLineId = 502,
                OrderId = orderId,
                ItemId = itemId,
                ItemName = "Товар",
                QtyOrdered = 5,
                QtyReceived = 0,
                QtyRemaining = 5
            }
        ]);
        store.As<IOptimizedOrderReadModelStore>()
            .Setup(s => s.GetUnlinkedProductionTotalsByItem(orderId))
            .Returns(new Dictionary<long, double> { [itemId] = 6 });

        var result = new DocumentService(store.Object).GetOrderReceiptRemaining(orderId);

        Assert.Collection(
            result.OrderBy(line => line.OrderLineId),
            first =>
            {
                Assert.Equal(501, first.OrderLineId);
                Assert.Equal(5, first.QtyReceived);
                Assert.Equal(0, first.QtyRemaining);
            },
            second =>
            {
                Assert.Equal(502, second.OrderLineId);
                Assert.Equal(5, second.QtyReceived);
                Assert.Equal(0, second.QtyRemaining);
            });
    }
}
