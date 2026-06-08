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

    [Fact]
    public void Build_OrderCoveredLikeProductionOrder113_HasNoCompatibleLines()
    {
        var harness = CreateHarness();
        harness.SeedItem(new Item { Id = 8, Name = "Товар C", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 9, Name = "Товар D", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 10, Name = "Товар E", BaseUom = "шт" });
        SeedCustomerOrder(harness, 113, 309, ItemId, 1200, OrderStatus.InProgress);
        SeedOrderLine(harness, 113, 310, OtherItemId, 1134);
        SeedOrderLine(harness, 113, 311, 8, 1824);
        SeedOrderLine(harness, 113, 312, 9, 3648);
        SeedOrderLine(harness, 113, 313, 10, 378);

        SeedProductionPallet(harness, 113, 309, ItemId, 600, ProductionPalletStatus.Filled, palletId: 1, docId: 1001, withLedger: true);
        SeedProductionPallet(harness, 113, 309, ItemId, 600, ProductionPalletStatus.Printed, palletId: 2, docId: 1001);
        SeedProductionPallet(harness, 113, 310, OtherItemId, 378, ProductionPalletStatus.Filled, palletId: 3, docId: 1002, withLedger: true);
        SeedProductionPallet(harness, 113, 310, OtherItemId, 378, ProductionPalletStatus.Filled, palletId: 4, docId: 1002, withLedger: true);
        SeedProductionPallet(harness, 113, 310, OtherItemId, 378, ProductionPalletStatus.Filled, palletId: 5, docId: 1002, withLedger: true);
        SeedProductionPallet(harness, 113, 312, 9, 1824, ProductionPalletStatus.Filled, palletId: 6, docId: 1003, withLedger: true);
        SeedProductionPallet(harness, 113, 312, 9, 1824, ProductionPalletStatus.Filled, palletId: 7, docId: 1003, withLedger: true);
        harness.SeedOrderReceiptPlanLines(
            113,
            PlanLine(113, 311, 8, "HU-BOUND-311", 1824),
            PlanLine(113, 313, 10, "HU-BOUND-313", 378));

        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY-A");
        harness.SeedBalance(OtherItemId, LocationId, 378, "HU-READY-B");
        harness.SeedBalance(8, LocationId, 1824, "HU-READY-C");
        harness.SeedBalance(9, LocationId, 1824, "HU-READY-D");
        harness.SeedBalance(10, LocationId, 378, "HU-READY-E");

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_ProducedAndOpenPrintedPalletCoverOrderLine_HasNoCompatibleLines()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 1200, OrderStatus.InProgress);
        SeedProductionPallet(harness, 10, 101, ItemId, 600, ProductionPalletStatus.Filled, palletId: 1, docId: 1001, withLedger: true);
        SeedProductionPallet(harness, 10, 101, ItemId, 600, ProductionPalletStatus.Printed, palletId: 2, docId: 1001);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_PartiallyProducedLine_OffersOnlyRealDeficit()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 1200, OrderStatus.InProgress);
        SeedProductionPallet(harness, 10, 101, ItemId, 600, ProductionPalletStatus.Filled, palletId: 1, docId: 1001, withLedger: true);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");
        harness.SeedBalance(ItemId, LocationId, 700, "HU-TOO-BIG");

        var result = Build(harness);

        var hu = Assert.Single(result.HuRows, row => row.HuCode == "HU-READY");
        Assert.DoesNotContain(result.HuRows, row => row.HuCode == "HU-TOO-BIG");
        Assert.Equal(600, Assert.Single(Assert.Single(hu.CompatibleOrders).Lines).MaxAdditionalBindQty, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_DraftLegacyReceipt_DoesNotReduceDeficit_AndMissingShipmentRowDoesNotExcludeLine(bool withLedger)
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 600, OrderStatus.InProgress);
        harness.SeedShippedTotalsByOrderLine(10, new Dictionary<long, double> { [101] = 600 });
        harness.SeedDoc(new Doc
        {
            Id = 1001,
            DocRef = "PRD-DRAFT",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10
        });
        harness.SeedLine(new DocLine
        {
            Id = 10001,
            DocId = 1001,
            OrderLineId = 101,
            ItemId = ItemId,
            Qty = 600,
            ToLocationId = LocationId,
            ToHu = "HU-DRAFT"
        });
        if (withLedger)
        {
            harness.SeedLedgerEntry(1001, ItemId, LocationId, 600, "HU-DRAFT");
        }

        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        var line = Assert.Single(Assert.Single(Assert.Single(result.HuRows, row => row.HuCode == "HU-READY").CompatibleOrders).Lines);
        Assert.Equal(0, line.ShipmentRemainingQty, 3);
        Assert.Equal(600, line.MaxAdditionalBindQty, 3);
    }

    [Fact]
    public void Build_ClosedLegacyReceiptWithLedger_ReducesDeficit()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 1200, OrderStatus.InProgress);
        harness.SeedDoc(new Doc
        {
            Id = 1001,
            DocRef = "PRD-CLOSED",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 10
        });
        harness.SeedLine(new DocLine
        {
            Id = 10001,
            DocId = 1001,
            OrderLineId = 101,
            ItemId = ItemId,
            Qty = 600,
            ToLocationId = LocationId,
            ToHu = "HU-PRODUCED"
        });
        harness.SeedLedgerEntry(1001, ItemId, LocationId, 600, "HU-PRODUCED");
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        Assert.Equal(600, Assert.Single(Assert.Single(Assert.Single(result.HuRows, row => row.HuCode == "HU-READY").CompatibleOrders).Lines).MaxAdditionalBindQty, 3);
    }

    [Fact]
    public void Build_OpenMixedPalletComponentsCoverTheirOrderLines()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 600, OrderStatus.InProgress);
        SeedOrderLine(harness, 10, 102, OtherItemId, 300);
        harness.SeedDoc(new Doc
        {
            Id = 1001,
            DocRef = "PRD-MIX",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 1001,
            OrderId = 10,
            HuCode = "HU-MIX",
            Status = ProductionPalletStatus.Planned,
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 101, ItemId = ItemId, PlannedQty = 600 },
                new ProductionPalletComponentLine { OrderLineId = 102, ItemId = OtherItemId, PlannedQty = 300 }
            ]
        });
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY-A");
        harness.SeedBalance(OtherItemId, LocationId, 300, "HU-READY-B");

        var result = Build(harness);

        Assert.Empty(result.HuRows);
    }

    [Fact]
    public void Build_ClosedPrdPlannedPallet_DoesNotCountAsOpenCoverage()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 600, OrderStatus.InProgress);
        SeedProductionPallet(
            harness,
            10,
            101,
            ItemId,
            600,
            ProductionPalletStatus.Planned,
            palletId: 1,
            docId: 1001,
            docStatus: DocStatus.Closed);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        Assert.Equal(600, Assert.Single(Assert.Single(Assert.Single(result.HuRows).CompatibleOrders).Lines).MaxAdditionalBindQty, 3);
    }

    [Fact]
    public void Build_CancelledPallet_DoesNotCountAsOpenCoverage()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 10, 101, ItemId, 600, OrderStatus.InProgress);
        SeedProductionPallet(harness, 10, 101, ItemId, 600, ProductionPalletStatus.Cancelled, palletId: 1, docId: 1001);
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");

        var result = Build(harness);

        Assert.Equal(600, Assert.Single(Assert.Single(Assert.Single(result.HuRows).CompatibleOrders).Lines).MaxAdditionalBindQty, 3);
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

    private static void SeedOrderLine(CloseDocumentHarness harness, long orderId, long lineId, long itemId, double qty)
    {
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qty
        });
    }

    private static void SeedProductionPallet(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long itemId,
        double qty,
        string status,
        long palletId,
        long docId,
        bool withLedger = false,
        DocStatus docStatus = DocStatus.Draft)
    {
        var huCode = $"HU-PRD-{palletId:000}";
        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = $"PRD-{docId}",
            Type = DocType.ProductionReceipt,
            Status = docStatus,
            OrderId = orderId
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = palletId,
            PrdDocId = docId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            HuCode = huCode,
            PlannedQty = qty,
            ToLocationId = LocationId,
            Status = status
        });
        if (withLedger)
        {
            harness.SeedLedgerEntry(docId, itemId, LocationId, qty, huCode);
        }
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
