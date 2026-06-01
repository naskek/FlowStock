using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class ReadyHuBindingReadModelTests
{
    [Fact]
    public void Build_FreeLedgerHuWithMatchingActiveCustomerOrder_Appears()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, orderId: 10, lineId: 101, itemId: ItemId, qty: 600, OrderStatus.InProgress);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        var hu = Assert.Single(result.HuRows);
        Assert.Equal("HU-READY", hu.HuCode);
        Assert.Equal(ItemId, hu.ItemId);
        Assert.Equal("Товар A", hu.ItemName);
        Assert.Equal(600, hu.Qty, 3);
        var order = Assert.Single(hu.CompatibleOrders);
        Assert.Equal(10, order.OrderId);
        var line = Assert.Single(order.Lines);
        Assert.Equal(101, line.OrderLineId);
        Assert.Equal(600, line.MaxAdditionalBindQty, 3);
    }

    [Fact]
    public void Build_HuAlreadyBoundThroughReceiptPlan_DoesNotAppear()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, orderId: 10, lineId: 101, itemId: ItemId, qty: 600, OrderStatus.InProgress);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-BOUND");
        harness.SeedOrderReceiptPlanLines(
            10,
            PlanLine(orderId: 10, lineId: 101, itemId: ItemId, huCode: "HU-BOUND", qty: 600));

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_HuBoundToAnotherActiveCustomerOrder_IsExcludedFromGlobalReadModel()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, orderId: 10, lineId: 101, itemId: ItemId, qty: 600, OrderStatus.InProgress);
        SeedCustomerOrder(harness, orderId: 20, lineId: 201, itemId: ItemId, qty: 600, OrderStatus.Accepted);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-BUSY");
        harness.SeedOrderReceiptPlanLines(
            20,
            PlanLine(orderId: 20, lineId: 201, itemId: ItemId, huCode: "HU-BUSY", qty: 600));

        var result = Build(harness);

        Assert.DoesNotContain(result.HuRows, row => row.HuCode == "HU-BUSY");
        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_HuWithoutMatchingActiveCustomerOrders_DoesNotAppear()
    {
        var harness = CreateHarness();
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Merged)]
    public void Build_InactiveCustomerOrderStatuses_AreIgnored(OrderStatus status)
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, orderId: 10, lineId: 101, itemId: ItemId, qty: 600, status);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_ItemMismatch_IsIgnored()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, orderId: 10, lineId: 101, itemId: OtherItemId, qty: 600, OrderStatus.InProgress);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-WRONG-ITEM");

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_ExistingBoundQtyReducesCompatibleLineCapacity()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, orderId: 10, lineId: 101, itemId: ItemId, qty: 1200, OrderStatus.InProgress);
        harness.SeedOrderReceiptPlanLines(
            10,
            PlanLine(orderId: 10, lineId: 101, itemId: ItemId, huCode: "HU-OLD", qty: 600));
        harness.SeedBalance(ItemId, LocationId, 600, "HU-OLD");
        harness.SeedBalance(ItemId, LocationId, 500, "HU-SMALL");
        harness.SeedBalance(ItemId, LocationId, 700, "HU-BIG");

        var result = Build(harness);

        var hu = Assert.Single(result.HuRows);
        Assert.Equal("HU-SMALL", hu.HuCode);
        var line = Assert.Single(Assert.Single(hu.CompatibleOrders).Lines);
        Assert.Equal(600, line.CurrentBoundQty, 3);
        Assert.Equal(["HU-OLD"], line.CurrentBoundHuCodes);
        Assert.Equal(600, line.MaxAdditionalBindQty, 3);
    }

    private static ReadyHuBindingReadModel Build(CloseDocumentHarness harness) =>
        new ReadyHuBindingReadModelService(harness.Store).Build();

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = LocationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = ItemId, Name = "Товар A", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = OtherItemId, Name = "Товар B", BaseUom = "шт" });
        return harness;
    }

    private static void SeedCustomerOrder(
        CloseDocumentHarness harness,
        long orderId,
        long lineId,
        long itemId,
        double qty,
        OrderStatus status)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = $"SO-{orderId:000}",
            Type = OrderType.Customer,
            Status = status,
            PartnerId = 1,
            CreatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qty
        });
    }

    private static OrderReceiptPlanLine PlanLine(long orderId, long lineId, long itemId, string huCode, double qty) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = itemId,
            QtyPlanned = qty,
            ToHu = huCode
        };

    private const long ItemId = 6;
    private const long OtherItemId = 7;
    private const long LocationId = 1;
}
