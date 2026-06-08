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

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной" });
        harness.SeedItem(new Item { Id = 6, Name = "Хрен 1 кг", BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 7, Name = "Хрен 200 г", BaseUom = "шт" });
        SeedOrder(harness, 112, OrderType.Internal);
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

    private static void SeedOrder(CloseDocumentHarness harness, long id, OrderType type)
    {
        harness.SeedOrder(new Order
        {
            Id = id,
            OrderRef = id.ToString(),
            Type = type,
            Status = OrderStatus.InProgress,
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
