using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class InternalOrderReceiptPlanRebuildTests
{
    [Fact]
    public void UpdateInternalOrder_WhenQtyReduced_TrimsExistingPlanToRemainingWithoutAllocatingHu()
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
            new() { Id = orderLineId, OrderId = orderId, ItemId = itemId, QtyOrdered = 400 }
        };

        // Уже существующий legacy-план: 4 HU по 100 (всего 400).
        var existingPlan = new List<OrderReceiptPlanLine>
        {
            PlanLine(orderId, orderLineId, itemId, "HU-A", 100, 0),
            PlanLine(orderId, orderLineId, itemId, "HU-B", 100, 1),
            PlanLine(orderId, orderLineId, itemId, "HU-C", 100, 2),
            PlanLine(orderId, orderLineId, itemId, "HU-D", 100, 3)
        };

        IReadOnlyList<OrderReceiptPlanLine>? capturedTrim = null;
        var store = CreatePlanningStore(order, orderLines, producedQty, existingPlan,
            trim => capturedTrim = trim, fullReplace => { });

        new OrderService(store.Object).UpdateOrder(
            orderId, order.OrderRef, null, null, null,
            [new OrderLineView { ItemId = itemId, ItemName = "Item 1", QtyOrdered = newOrderedQty }],
            OrderType.Internal);

        // targetRemaining = newOrdered(200) − produced(100) = 100 → остаётся одно существующее HU, остальные surplus отброшены.
        Assert.NotNull(capturedTrim);
        var kept = Assert.Single(capturedTrim!);
        Assert.Equal("HU-A", kept.ToHu);
        Assert.Equal(100d, kept.QtyPlanned, 3);
        // Существующие HU-коды сохранены; новые HU не выделяются; полная перестройка плана не вызывается.
        store.Verify(s => s.GetHus(It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        store.Verify(s => s.ReplaceOrderReceiptPlanLines(order.Id, It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()), Times.Never);
    }

    [Fact]
    public void UpdateInternalOrder_WhenQtyReduced_WithoutExistingPlan_DoesNotAllocateOrRebuild()
    {
        const long orderId = 17;
        const long orderLineId = 1701;
        const long itemId = 1001;

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
            new() { Id = orderLineId, OrderId = orderId, ItemId = itemId, QtyOrdered = 400 }
        };

        IReadOnlyList<OrderReceiptPlanLine>? capturedTrim = null;
        var store = CreatePlanningStore(order, orderLines, producedQty: 100d, Array.Empty<OrderReceiptPlanLine>(),
            trim => capturedTrim = trim, fullReplace => { });

        new OrderService(store.Object).UpdateOrder(
            orderId, order.OrderRef, null, null, null,
            [new OrderLineView { ItemId = itemId, ItemName = "Item 1", QtyOrdered = 200 }],
            OrderType.Internal);

        Assert.Null(capturedTrim);
        store.Verify(s => s.GetHus(It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        store.Verify(s => s.ReplaceOrderReceiptPlanLines(order.Id, It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()), Times.Never);
        store.Verify(s => s.ReplaceOrderReceiptPlanLinesForOrderLines(
            order.Id, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()), Times.Never);
    }

    [Fact]
    public void UpdateInternalOrder_WhenQtyIncreased_DoesNotRebuildOrAllocate()
    {
        const long orderId = 17;
        const long orderLineId = 1701;
        const long itemId = 1001;

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
            new() { Id = orderLineId, OrderId = orderId, ItemId = itemId, QtyOrdered = 400 }
        };

        IReadOnlyList<OrderReceiptPlanLine>? capturedTrim = null;
        var store = CreatePlanningStore(order, orderLines, producedQty: 100d, Array.Empty<OrderReceiptPlanLine>(),
            trim => capturedTrim = trim, fullReplace => { });

        new OrderService(store.Object).UpdateOrder(
            orderId, order.OrderRef, null, null, null,
            [new OrderLineView { ItemId = itemId, ItemName = "Item 1", QtyOrdered = 500 }],
            OrderType.Internal);

        Assert.Null(capturedTrim);
        store.Verify(s => s.ReplaceOrderReceiptPlanLines(order.Id, It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()), Times.Never);
        store.Verify(s => s.GetHus(It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void CreateInternalOrder_DoesNotBuildReceiptPlanOrPallets()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "01", Name = "Склад", AutoHuDistributionEnabled = true });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Товар", EnableHuDistribution = true });
        harness.SeedItem(new Item { Id = 1001, Name = "Item 1", BaseUom = "шт", ItemTypeId = 1, MaxQtyPerHu = 600 });

        var orderId = new OrderService(harness.Store).CreateOrder(
            "INT-NEW", null, null, null,
            [new OrderLineView { ItemId = 1001, ItemName = "Item 1", QtyOrdered = 1800 }],
            OrderType.Internal);

        Assert.NotEqual(0, orderId);
        Assert.NotEmpty(harness.Store.GetOrderLines(orderId));
        Assert.Empty(harness.GetOrderReceiptPlanLines(orderId));
        Assert.Empty(harness.Store.GetDocsByOrder(orderId));
    }

    private static OrderReceiptPlanLine PlanLine(long orderId, long lineId, long itemId, string huCode, double qty, int sortOrder) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = itemId,
            QtyPlanned = qty,
            ToHu = huCode,
            SortOrder = sortOrder
        };

    private static Mock<IDataStore> CreatePlanningStore(
        Order order,
        List<OrderLine> orderLines,
        double producedQty,
        IReadOnlyList<OrderReceiptPlanLine> existingPlan,
        Action<IReadOnlyList<OrderReceiptPlanLine>> captureTrim,
        Action<IReadOnlyList<OrderReceiptPlanLine>> captureFullReplace)
    {
        var currentQtyOrdered = orderLines[0].QtyOrdered;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(order.Id)).Returns(order);
        store.Setup(s => s.GetOrderLines(order.Id))
            .Returns(() =>
            [
                new OrderLine
                {
                    Id = orderLines[0].Id,
                    OrderId = orderLines[0].OrderId,
                    ItemId = orderLines[0].ItemId,
                    QtyOrdered = currentQtyOrdered
                }
            ]);
        store.Setup(s => s.GetOrderReceiptPlanLines(order.Id)).Returns(existingPlan);
        store.Setup(s => s.UpdateOrder(It.IsAny<Order>()));
        store.Setup(s => s.UpdateOrderLineQty(orderLines[0].Id, It.IsAny<double>()))
            .Callback<long, double>((_, qty) => currentQtyOrdered = qty);
        store.Setup(s => s.GetShippedTotalsByOrderLine(order.Id)).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetFilledProductionPalletQtyByOrderLine(orderLines[0].Id, null)).Returns(0);
        store.Setup(s => s.ClearPlannedProductionPalletPlanForOrderLines(
                order.Id,
                It.Is<IReadOnlyCollection<long>>(ids => ids.Contains(orderLines[0].Id))))
            .Returns(new ProductionPalletPlanCleanupCounts());
        store.Setup(s => s.GetDocsByOrder(order.Id)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptRemaining(order.Id))
            .Returns(() =>
            [
                new OrderReceiptLine
                {
                    OrderLineId = orderLines[0].Id,
                    OrderId = order.Id,
                    ItemId = orderLines[0].ItemId,
                    QtyOrdered = currentQtyOrdered,
                    QtyReceived = producedQty,
                    QtyRemaining = Math.Max(0, currentQtyOrdered - producedQty)
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
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((_, lines) => captureFullReplace(lines));
        store.Setup(s => s.ReplaceOrderReceiptPlanLinesForOrderLines(
                order.Id,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyCollection<long>, IReadOnlyList<OrderReceiptPlanLine>>((_, __, lines) => captureTrim(lines));
        return store;
    }
}
