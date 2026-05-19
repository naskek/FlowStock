using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class InternalOrderMergeTests
{
    [Fact]
    public void Evaluate_WhenActivePalletPlanRemains_ReturnsActivePalletWarning()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedOrder(new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.Now
        });
        harness.SeedOrderLine(new OrderLine { Id = 171, OrderId = 66, ItemId = 100, QtyOrdered = 0 });
        harness.SeedDoc(new Doc
        {
            Id = 162,
            DocRef = "PRD-066",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 66,
            CreatedAt = DateTime.Now
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 162,
            DocLineId = 1,
            OrderId = 66,
            OrderLineId = 171,
            ItemId = 100,
            HuCode = "HU-1",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.Now
        });

        var evaluation = InternalOrderMergeService.Evaluate(harness.Store, 66);

        Assert.Equal(InternalOrderMergeService.ActivePalletPlanWarningCode, evaluation.WarningCode);
        Assert.False(evaluation.CanMerge);
    }

    [Fact]
    public void TryMarkAsMerged_WhenEligible_UpdatesStatusAndCommentIdempotently()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedOrder(new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            Comment = "Старый комментарий",
            CreatedAt = DateTime.Now
        });
        harness.SeedOrderLine(new OrderLine { Id = 171, OrderId = 66, ItemId = 100, QtyOrdered = 0 });

        var first = InternalOrderMergeService.TryMarkAsMerged(harness.Store, 66, 67, "067");
        var second = InternalOrderMergeService.TryMarkAsMerged(harness.Store, 66, 67, "067");

        Assert.True(first.IsMerged);
        Assert.True(first.CommentUpdated);
        Assert.True(second.IsMerged);
        Assert.False(second.CommentUpdated);
        var order = harness.Store.GetOrder(66);
        Assert.Equal(OrderStatus.Merged, order?.Status);
        Assert.Equal(1, (order?.Comment ?? string.Empty).Split("Объединён с заказом", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RefreshPersistedStatus_WhenAlreadyMerged_PreservesMerged()
    {
        const long orderId = 66;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId)).Returns(new Order
        {
            Id = orderId,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.Merged,
            CreatedAt = DateTime.Now
        });
        store.Setup(s => s.UpdateOrderStatus(orderId, OrderStatus.Merged));

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.Equal(OrderStatus.Merged, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Merged), Times.Once);
        store.Verify(s => s.GetOrderLines(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public void ApplyFromOpenInternalOrders_ExcludesMergedInternalCandidates()
    {
        const long internalOrderId = 66;
        const long customerOrderId = 67;

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(customerOrderId)).Returns(new Order
        {
            Id = customerOrderId,
            OrderRef = "067",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = DateTime.Now
        });
        store.Setup(s => s.GetOrderLines(customerOrderId)).Returns([
            new OrderLine { Id = 172, OrderId = customerOrderId, ItemId = 100, QtyOrdered = 100 }
        ]);
        store.Setup(s => s.GetOrders()).Returns([
            new Order
            {
                Id = internalOrderId,
                OrderRef = "066",
                Type = OrderType.Internal,
                Status = OrderStatus.Merged,
                CreatedAt = DateTime.Now.AddDays(-1)
            },
            new Order
            {
                Id = customerOrderId,
                OrderRef = "067",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress,
                UseReservedStock = true,
                CreatedAt = DateTime.Now
            }
        ]);

        var service = new OrderAutoRedistributionService(store.Object);
        var result = service.ApplyFromOpenInternalOrders(customerOrderId);

        Assert.False(result.HasTransfers);
        Assert.Equal("NO_OPEN_INTERNAL_ORDERS", result.SkippedReason);
    }
}
