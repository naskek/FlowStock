using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class InternalOrderReceiptPlanRebuildTests
{
    [Fact]
    public void UpdateInternalOrder_WhenQtyReduced_RebuildsPlanForRemainingToProduceOnly()
    {
        const long orderId = 17;
        const long orderLineId = 1701;
        const long itemId = 1001;
        const double producedQty = 100d;
        const double newOrderedQty = 200d;

        var order = new Order
        {
            Id = orderId,
            OrderRef = "INT-17",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 1, 1)
        };

        var orderLines = new List<OrderLine>
        {
            new()
            {
                Id = orderLineId,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = 400
            }
        };

        IReadOnlyList<OrderReceiptPlanLine>? capturedPlan = null;
        var store = CreatePlanningStore(order, orderLines, producedQty, plan => capturedPlan = plan);

        var service = new OrderService(store.Object);
        service.UpdateOrder(
            orderId,
            order.OrderRef,
            null,
            null,
            null,
            [new OrderLineView { ItemId = itemId, ItemName = "Item 1", QtyOrdered = newOrderedQty }],
            OrderType.Internal);

        Assert.NotNull(capturedPlan);
        Assert.Single(capturedPlan!);
        Assert.Equal(100d, capturedPlan!.Sum(line => line.QtyPlanned), 3);
    }

    private static Mock<IDataStore> CreatePlanningStore(
        Order order,
        List<OrderLine> orderLines,
        double producedQty,
        Action<IReadOnlyList<OrderReceiptPlanLine>> capturePlan)
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(order.Id)).Returns(order);
        store.Setup(s => s.GetOrderLines(order.Id)).Returns(() => orderLines);
        store.Setup(s => s.UpdateOrder(It.IsAny<Order>()));
        store.Setup(s => s.UpdateOrderLineQty(orderLines[0].Id, It.IsAny<double>()));
        store.Setup(s => s.GetShippedTotalsByOrderLine(order.Id)).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetDocsByOrder(order.Id)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptRemaining(order.Id))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = orderLines[0].Id,
                    OrderId = order.Id,
                    ItemId = orderLines[0].ItemId,
                    QtyOrdered = orderLines[0].QtyOrdered,
                    QtyReceived = producedQty,
                    QtyRemaining = Math.Max(0, orderLines[0].QtyOrdered - producedQty)
                }
            ]);
        store.Setup(s => s.FindItemById(orderLines[0].ItemId))
            .Returns(new Item
            {
                Id = orderLines[0].ItemId,
                Name = "Item 1",
                ItemTypeId = 1,
                MaxQtyPerHu = 1000
            });
        store.Setup(s => s.GetItemType(1))
            .Returns(new ItemType { Id = 1, Name = "Товар", EnableHuDistribution = false });
        store.Setup(s => s.GetLocations())
            .Returns([new Location { Id = 1, Code = "01", AutoHuDistributionEnabled = true }]);
        store.Setup(s => s.ReplaceOrderReceiptPlanLines(order.Id, It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((_, lines) => capturePlan(lines));
        return store;
    }
}
