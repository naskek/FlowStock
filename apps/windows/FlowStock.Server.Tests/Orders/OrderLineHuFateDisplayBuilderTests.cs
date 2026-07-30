using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineHuFateDisplayBuilderTests
{
    [Fact]
    public void BuildByOrder_ReservedHu_IsSymmetricAcrossSourceAndTargetOrders()
    {
        var harness = CreateHarness();
        SeedFilledPallet(harness, "HU-0000766", qty: 378);
        harness.SeedOrderReceiptPlanLines(115, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 115,
            OrderLineId = 1151,
            ItemId = 6,
            QtyPlanned = 378,
            ToHu = "hu-0000766"
        });

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);
        var targetRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 115)[1151]);

        Assert.Equal("HU-0000766 · наполнено · 378 → резерв заказ 115", ToRow(sourceRow).DisplayText);
        Assert.Equal("HU-0000766 · резерв · 378 ← выпуск заказ 112", ToRow(targetRow).DisplayText);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedFateCode, sourceRow.FateCode);
        Assert.Equal("→ резерв заказ 115", sourceRow.FateLabel);
        Assert.Equal("115", sourceRow.FateOrderRef);
        Assert.Equal(378, sourceRow.FateQty);
    }

    [Fact]
    public void BuildByOrder_ShippedHu_IsSymmetricAndVisibleAtZeroBalance()
    {
        var harness = CreateHarness();
        SeedFilledPallet(harness, "HU-0000709", qty: 600);
        harness.SeedDoc(new Doc
        {
            Id = 200,
            DocRef = "OUT-200",
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = 107,
            CreatedAt = new DateTime(2026, 5, 2, 8, 0, 0),
            ClosedAt = new DateTime(2026, 5, 2, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 200,
            OrderLineId = 1071,
            ItemId = 6,
            Qty = 600,
            FromHu = "HU-0000709"
        });
        harness.SeedLedgerEntry(200, 6, 1, -600, "HU-0000709");

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);
        var targetRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 107)[1071]);

        Assert.Equal("HU-0000709 · наполнено · 600 → отгружено заказ 107", ToRow(sourceRow).DisplayText);
        Assert.Equal("HU-0000709 · отгружено · 600 ← выпуск заказ 112", ToRow(targetRow).DisplayText);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, sourceRow.FateCode);
        Assert.Equal("→ отгружено заказ 107", sourceRow.FateLabel);
        Assert.Equal("107", sourceRow.FateOrderRef);
        Assert.Equal("OUT-200", sourceRow.FateDocRef);
        Assert.Equal(600, sourceRow.FateQty);
    }

    [Fact]
    public void BuildByOrder_SourceDisplay_PrefersLatestShipmentOverActiveReservation()
    {
        var harness = CreateHarness();
        SeedFilledPallet(harness, "HU-FATE", qty: 600);
        harness.SeedOrderReceiptPlanLines(115, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 115,
            OrderLineId = 1151,
            ItemId = 6,
            QtyPlanned = 500,
            ToHu = "HU-FATE"
        });
        SeedOutbound(harness, docId: 200, targetOrderId: 107, targetLineId: 1071, qty: 50, "HU-FATE", closedHour: 9);
        SeedOutbound(harness, docId: 201, targetOrderId: 115, targetLineId: 1151, qty: 50, "HU-FATE", closedHour: 10);

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);

        Assert.Equal("HU-FATE · наполнено · 600 → отгружено заказ 115", ToRow(sourceRow).DisplayText);
        Assert.Equal("OUT-201", sourceRow.FateDocRef);
        Assert.Equal(50, sourceRow.FateQty);
    }

    [Fact]
    public void BuildByOrder_SourceDisplay_ExposesCurrentStockAsStructuredFateWithoutChangingWpfText()
    {
        var harness = CreateHarness();
        SeedFilledPallet(harness, "HU-STOCK", qty: 600);

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);

        Assert.Equal("HU-STOCK · наполнено · 600", ToRow(sourceRow).DisplayText);
        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, sourceRow.FateCode);
        Assert.Equal("на складе", sourceRow.FateLabel);
        Assert.Equal(600, sourceRow.FateQty);
    }

    [Theory]
    [InlineData(OrderStatus.Draft, false)]
    [InlineData(OrderStatus.InProgress, true)]
    [InlineData(OrderStatus.Accepted, true)]
    [InlineData(OrderStatus.Shipped, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Merged, false)]
    public void BuildByOrder_CustomerOwnFilledHu_UsesAwaitingShipmentOnlyForOutboundLifecycleStatuses(
        OrderStatus status,
        bool expectedAwaitingShipment)
    {
        var harness = CreateHarness(OrderType.Customer, status);
        SeedFilledPallet(harness, "HU-CUSTOMER", qty: 600);

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);

        Assert.Equal(
            expectedAwaitingShipment
                ? OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode
                : OrderLineHuFateDisplayBuilder.OnStockFateCode,
            sourceRow.FateCode);
        Assert.Equal(
            expectedAwaitingShipment
                ? OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel
                : "на складе",
            sourceRow.FateLabel);
        Assert.Equal(
            expectedAwaitingShipment ? "Ожидает отгрузки" : "наполнено",
            sourceRow.Label);
        Assert.Equal(600, sourceRow.FateQty);
    }

    [Fact]
    public void BuildByOrder_CustomerOwnFilledHu_SameOrderReservationKeepsReservedPriority()
    {
        var harness = CreateHarness(OrderType.Customer);
        SeedFilledPallet(harness, "HU-CUSTOMER-RESERVED", qty: 600);
        harness.SeedOrderReceiptPlanLines(112, new OrderReceiptPlanLine
        {
            Id = 10,
            OrderId = 112,
            OrderLineId = 1121,
            ItemId = 6,
            QtyPlanned = 600,
            ToHu = "HU-CUSTOMER-RESERVED"
        });

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedFateCode, sourceRow.FateCode);
        Assert.Equal("резерв этого заказа", sourceRow.FateLabel);
    }

    [Fact]
    public void BuildByOrder_CustomerFilledPalletWithoutExplicitOwner_DoesNotAwaitShipment()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 14,
            PrdDocId = 100,
            OrderId = null,
            OrderLineId = 1121,
            ItemId = 6,
            HuCode = "HU-OWNER-NULL",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        harness.SeedLedgerEntry(100, 6, 1, 600, "HU-OWNER-NULL");

        var row = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, row.FateCode);
        Assert.NotEqual(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, row.FateCode);
    }

    [Fact]
    public void BuildByOrder_CustomerFilledPalletWithForeignComponentLine_DoesNotAwaitShipment()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 15,
            PrdDocId = 100,
            OrderId = 112,
            OrderLineId = 1071,
            ItemId = 6,
            HuCode = "HU-FOREIGN-LINE",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        harness.SeedLedgerEntry(100, 6, 1, 600, "HU-FOREIGN-LINE");

        var row = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1071]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, row.FateCode);
        Assert.NotEqual(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, row.FateCode);
    }

    [Fact]
    public void BuildByOrder_CustomerFilledPalletOwnedByAnotherOrder_IsNotAwaitingForPrdOrder()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 16,
            PrdDocId = 100,
            OrderId = 107,
            OrderLineId = 1121,
            ItemId = 6,
            HuCode = "HU-FOREIGN-OWNER",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        harness.SeedLedgerEntry(100, 6, 1, 600, "HU-FOREIGN-OWNER");

        var rows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);

        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void BuildByOrder_MixedFilledAndPrintedRows_AreBothVisible()
    {
        var harness = CreateHarness();
        SeedFilledPallet(harness, "HU-0000764", qty: 378);
        SeedFilledPallet(harness, "HU-0000765", qty: 378);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 13,
            PrdDocId = 100,
            OrderId = 112,
            OrderLineId = 1121,
            ItemId = 6,
            HuCode = "HU-0000763",
            PlannedQty = 378,
            Status = ProductionPalletStatus.Printed,
            CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0)
        });

        var line = new OrderLineView
        {
            ProductionHuDisplayEntries = ProductionOrderLineHuCodes.BuildProductionDisplayByOrder(harness.Store, 112)[1121],
            HuFateDisplayEntries = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]
        };

        Assert.Equal(["HU-0000763", "HU-0000764", "HU-0000765"], line.HuDisplayRows.Select(row => row.HuCode));
        Assert.Equal(["напечатано", "наполнено", "наполнено"], line.HuDisplayRows.Select(row => row.Label));
    }

    [Fact]
    public void BuildByOrder_MixedPallet_ResolvesSourceFromComponentLine()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 20,
            PrdDocId = 100,
            OrderId = 112,
            ItemId = 6,
            HuCode = "HU-MIXED",
            PlannedQty = 500,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 1121, ItemId = 6, PlannedQty = 300, FilledQty = 300 },
                new ProductionPalletComponentLine { OrderLineId = 1122, ItemId = 7, PlannedQty = 200, FilledQty = 200 }
            ]
        });
        harness.SeedLedgerEntry(100, 6, 1, 300, "HU-MIXED");
        harness.SeedLedgerEntry(100, 7, 1, 200, "HU-MIXED");
        harness.SeedOrderReceiptPlanLines(115, new OrderReceiptPlanLine
        {
            Id = 2,
            OrderId = 115,
            OrderLineId = 1152,
            ItemId = 7,
            QtyPlanned = 200,
            ToHu = "HU-MIXED"
        });

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1122]);
        var targetRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 115)[1152]);

        Assert.Equal("HU-MIXED · наполнено · 200 → резерв заказ 115", ToRow(sourceRow).DisplayText);
        Assert.Equal("HU-MIXED · резерв · 200 ← выпуск заказ 112", ToRow(targetRow).DisplayText);
    }

    [Fact]
    public void BuildByOrder_CustomerMixedFilledHu_AwaitsOnlyWhenEveryComponentHasLedgerStock()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 21,
            PrdDocId = 100,
            OrderId = 112,
            ItemId = 6,
            HuCode = "HU-MIXED-READY",
            PlannedQty = 500,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 1121, ItemId = 6, PlannedQty = 300, FilledQty = 300 },
                new ProductionPalletComponentLine { OrderLineId = 1122, ItemId = 7, PlannedQty = 200, FilledQty = 200 }
            ]
        });
        harness.SeedLedgerEntry(100, 6, 1, 300, "HU-MIXED-READY");
        harness.SeedLedgerEntry(100, 7, 1, 200, "HU-MIXED-READY");

        var rows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);

        var first = Assert.Single(rows[1121]);
        var second = Assert.Single(rows[1122]);
        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, first.FateCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, second.FateCode);
        Assert.Equal(300, first.FateQty);
        Assert.Equal(200, second.FateQty);
    }

    [Fact]
    public void BuildByOrder_CustomerMixedFilledHu_ReservationOnOneComponentSuppressesAwaitingForWholePallet()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 24,
            PrdDocId = 100,
            OrderId = 112,
            ItemId = 6,
            HuCode = "HU-MIXED-RESERVED",
            PlannedQty = 500,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 1121, ItemId = 6, PlannedQty = 300, FilledQty = 300 },
                new ProductionPalletComponentLine { OrderLineId = 1122, ItemId = 7, PlannedQty = 200, FilledQty = 200 }
            ]
        });
        harness.SeedLedgerEntry(100, 6, 1, 300, "HU-MIXED-RESERVED");
        harness.SeedLedgerEntry(100, 7, 1, 200, "HU-MIXED-RESERVED");
        harness.SeedOrderReceiptPlanLines(115, new OrderReceiptPlanLine
        {
            Id = 20,
            OrderId = 115,
            OrderLineId = 1152,
            ItemId = 7,
            QtyPlanned = 200,
            ToHu = "HU-MIXED-RESERVED"
        });

        var rows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, Assert.Single(rows[1121]).FateCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedFateCode, Assert.Single(rows[1122]).FateCode);
        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void BuildByOrder_CustomerMixedFilledHu_ShipmentOnOneComponentSuppressesAwaitingForWholePallet()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 25,
            PrdDocId = 100,
            OrderId = 112,
            ItemId = 6,
            HuCode = "HU-MIXED-SHIPPED",
            PlannedQty = 500,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 1121, ItemId = 6, PlannedQty = 300, FilledQty = 300 },
                new ProductionPalletComponentLine { OrderLineId = 1122, ItemId = 7, PlannedQty = 200, FilledQty = 200 }
            ]
        });
        harness.SeedLedgerEntry(100, 6, 1, 300, "HU-MIXED-SHIPPED");
        harness.SeedLedgerEntry(100, 7, 1, 200, "HU-MIXED-SHIPPED");
        harness.SeedDoc(new Doc
        {
            Id = 205,
            DocRef = "OUT-205",
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = 115,
            CreatedAt = new DateTime(2026, 5, 2, 8, 0, 0),
            ClosedAt = new DateTime(2026, 5, 2, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 1205,
            DocId = 205,
            OrderLineId = 1152,
            ItemId = 7,
            Qty = 50,
            FromHu = "HU-MIXED-SHIPPED"
        });
        harness.SeedLedgerEntry(205, 7, 1, -50, "HU-MIXED-SHIPPED");

        var rows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, Assert.Single(rows[1121]).FateCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, Assert.Single(rows[1122]).FateCode);
        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void BuildByOrder_CustomerMixedFilledHu_MissingComponentStockSuppressesAwaitingForWholeHu()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 22,
            PrdDocId = 100,
            OrderId = 112,
            ItemId = 6,
            HuCode = "HU-MIXED-PARTIAL-STOCK",
            PlannedQty = 500,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 1121, ItemId = 6, PlannedQty = 300, FilledQty = 300 },
                new ProductionPalletComponentLine { OrderLineId = 1122, ItemId = 7, PlannedQty = 200, FilledQty = 200 }
            ]
        });
        harness.SeedLedgerEntry(100, 6, 1, 300, "HU-MIXED-PARTIAL-STOCK");

        var rows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, Assert.Single(rows[1121]).FateCode);
        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void BuildByOrder_CustomerMixedHu_IncompleteComponentSuppressesAwaitingEvenWithLedgerStock()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 23,
            PrdDocId = 100,
            OrderId = 112,
            ItemId = 6,
            HuCode = "HU-MIXED-INCOMPLETE",
            PlannedQty = 500,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine { OrderLineId = 1121, ItemId = 6, PlannedQty = 300, FilledQty = 300 },
                new ProductionPalletComponentLine { OrderLineId = 1122, ItemId = 7, PlannedQty = 200, FilledQty = 100 }
            ]
        });
        harness.SeedLedgerEntry(100, 6, 1, 300, "HU-MIXED-INCOMPLETE");
        harness.SeedLedgerEntry(100, 7, 1, 100, "HU-MIXED-INCOMPLETE");

        var rows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);

        Assert.All(
            rows.Values.SelectMany(entries => entries),
            entry => Assert.NotEqual(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, entry.FateCode));
    }

    [Fact]
    public void BuildByOrder_UnknownAndLegacyWithoutHu_DoNotInventSourceArrow()
    {
        var harness = CreateHarness();
        harness.SeedOrderReceiptPlanLines(115, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 115,
            OrderLineId = 1151,
            ItemId = 6,
            QtyPlanned = 100,
            ToHu = "HU-UNKNOWN"
        });
        harness.SeedLedgerEntry(900, 6, 1, 100, "HU-UNKNOWN");
        harness.SeedDoc(new Doc
        {
            Id = 300,
            DocRef = "PRD-LEGACY",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 112,
            CreatedAt = new DateTime(2026, 5, 1, 7, 0, 0),
            ClosedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        harness.SeedLine(new DocLine { Id = 301, DocId = 300, OrderLineId = 1121, ItemId = 6, Qty = 100 });
        harness.SeedLedgerEntry(300, 6, 1, 100);

        var sourceRows = OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112);
        var targetRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 115)[1151]);

        Assert.Empty(sourceRows);
        Assert.Equal("HU-UNKNOWN · резерв · 100", ToRow(targetRow).DisplayText);
    }

    [Fact]
    public void BuildByOrder_CustomerLegacyClosedPrdWithoutProductionPallet_RemainsOnStock()
    {
        var harness = CreateHarness(OrderType.Customer);
        harness.SeedDoc(new Doc
        {
            Id = 300,
            DocRef = "PRD-LEGACY",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 112,
            CreatedAt = new DateTime(2026, 5, 1, 7, 0, 0),
            ClosedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 300,
            OrderLineId = 1121,
            ItemId = 6,
            Qty = 100,
            ToHu = "HU-LEGACY"
        });
        harness.SeedLedgerEntry(300, 6, 1, 100, "HU-LEGACY");

        var sourceRow = Assert.Single(OrderLineHuFateDisplayBuilder.BuildByOrder(harness.Store, 112)[1121]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, sourceRow.FateCode);
        Assert.Equal("на складе", sourceRow.FateLabel);
    }

    private static CloseDocumentHarness CreateHarness(
        OrderType sourceOrderType = OrderType.Internal,
        OrderStatus sourceOrderStatus = OrderStatus.InProgress)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной" });
        harness.SeedItem(new Item { Id = 6, Name = "Хрен 1 кг", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 7, Name = "Хрен 200 г", BaseUom = "шт" });
        SeedOrder(harness, 112, sourceOrderType, sourceOrderStatus);
        SeedOrder(harness, 107, OrderType.Customer);
        SeedOrder(harness, 115, OrderType.Customer);
        harness.SeedOrderLine(new OrderLine { Id = 1121, OrderId = 112, ItemId = 6, QtyOrdered = 1200 });
        harness.SeedOrderLine(new OrderLine { Id = 1122, OrderId = 112, ItemId = 7, QtyOrdered = 200 });
        harness.SeedOrderLine(new OrderLine { Id = 1071, OrderId = 107, ItemId = 6, QtyOrdered = 600 });
        harness.SeedOrderLine(new OrderLine { Id = 1151, OrderId = 115, ItemId = 6, QtyOrdered = 378 });
        harness.SeedOrderLine(new OrderLine { Id = 1152, OrderId = 115, ItemId = 7, QtyOrdered = 200 });
        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-100",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 112,
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        return harness;
    }

    private static void SeedOrder(
        CloseDocumentHarness harness,
        long id,
        OrderType type,
        OrderStatus status = OrderStatus.InProgress)
    {
        harness.SeedOrder(new Order
        {
            Id = id,
            OrderRef = id.ToString(),
            Type = type,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
    }

    private static void SeedFilledPallet(CloseDocumentHarness harness, string huCode, double qty)
    {
        var id = Math.Abs(huCode.GetHashCode());
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = id,
            PrdDocId = 100,
            OrderId = 112,
            OrderLineId = 1121,
            ItemId = 6,
            HuCode = huCode,
            PlannedQty = qty,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 9, 0, 0),
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0)
        });
        harness.SeedLedgerEntry(100, 6, 1, qty, huCode);
    }

    private static void SeedOutbound(
        CloseDocumentHarness harness,
        long docId,
        long targetOrderId,
        long targetLineId,
        double qty,
        string huCode,
        int closedHour)
    {
        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = $"OUT-{docId}",
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = targetOrderId,
            CreatedAt = new DateTime(2026, 5, 2, 8, 0, 0),
            ClosedAt = new DateTime(2026, 5, 2, closedHour, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = docId + 1000,
            DocId = docId,
            OrderLineId = targetLineId,
            ItemId = 6,
            Qty = qty,
            FromHu = huCode
        });
        harness.SeedLedgerEntry(docId, 6, 1, -qty, huCode);
    }

    private static OrderLineHuDisplayRow ToRow(OrderLineHuDisplayEntry entry) =>
        new(entry.HuCode, entry.Label, entry.Qty, false, entry.SortOrder, entry.FateSuffix);
}
