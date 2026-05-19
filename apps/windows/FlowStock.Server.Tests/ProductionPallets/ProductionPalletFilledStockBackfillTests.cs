using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletFilledStockBackfillTests
{
    private const long TestItemId = 6;
    private const long TestLocationId = 1;
    private const long TestOrderId = 100;
    private const long TestPalletId = 439;

    [Fact]
    public void Backfill_Skips_WhenOutboundBySameHuExists()
    {
        var harness = CreateFilledPalletHarness(
            huCode: "HU-0000439",
            plannedQty: 600,
            currentLedgerQty: 0,
            outboundSameHuQty: 600,
            outboundDocRef: "OUT-2026-000165");
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var analysis = Assert.Single(service.GetStockAnalyses());
        Assert.Equal(ProductionPalletStockBackfillDecisionCodes.AlreadyShippedSkip, analysis.Decision);
        Assert.Equal(0, analysis.MissingQty);

        var apply = service.BackfillFilledStock(dryRun: false);
        Assert.Equal(0, apply.LedgerRowsWritten);
        Assert.Empty(apply.Applied);
        Assert.Empty(harness.LedgerEntries.Where(entry => entry.QtyDelta > 0));
    }

    [Fact]
    public void Backfill_Ambiguous_WhenOrderShippedButNoHuOutbound()
    {
        var harness = CreateFilledPalletHarness(
            huCode: "HU-0000439",
            plannedQty: 600,
            currentLedgerQty: 0,
            orderStatus: OrderStatus.Shipped);
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var analysis = Assert.Single(service.GetStockAnalyses());
        Assert.Equal(ProductionPalletStockBackfillDecisionCodes.AmbiguousRequiresManualReview, analysis.Decision);

        var apply = service.BackfillFilledStock(dryRun: false);
        Assert.Equal(0, apply.LedgerRowsWritten);
    }

    [Fact]
    public void Backfill_Safe_WhenFilledNotShipped()
    {
        var harness = CreateFilledPalletHarness(
            huCode: "HU-0000437",
            plannedQty: 840,
            currentLedgerQty: 0,
            orderStatus: OrderStatus.InProgress);
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var analysis = Assert.Single(service.GetStockAnalyses());
        Assert.Equal(ProductionPalletStockBackfillDecisionCodes.SafeToBackfill, analysis.Decision);
        Assert.Equal(840, analysis.MissingQty);

        var apply = service.BackfillFilledStock(dryRun: false);
        Assert.Equal(1, apply.LedgerRowsWritten);
        Assert.Equal(840, harness.Store.GetLedgerBalance(TestItemId, TestLocationId, "HU-0000437"), 3);
    }

    [Fact]
    public void ReverseCandidates_ReturnsAlreadyShippedWithCurrentStock()
    {
        var harness = CreateFilledPalletHarness(
            huCode: "HU-0000439",
            plannedQty: 600,
            currentLedgerQty: 0,
            outboundSameHuQty: 600,
            outboundDocRef: "OUT-2026-000165");
        harness.SeedLedgerEntry(153, TestItemId, TestLocationId, 600, "HU-0000439");
        harness.SeedLedgerEntry(192, TestItemId, TestLocationId, 600, "HU-0000439");
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var candidate = Assert.Single(service.GetReverseCandidates());
        Assert.Equal(TestPalletId, candidate.PalletId);
        Assert.Equal(600, candidate.CurrentHuStock);
        Assert.Equal(600, candidate.ReverseQty);
        Assert.Equal(600, candidate.OutboundBySameHuQty);
    }

    [Fact]
    public void ReverseBackfillDraft_CreatesInventoryCorrectionNegativeLines_AndCloseZeroesStock()
    {
        var harness = CreateFilledPalletHarness(
            huCode: "HU-0000439",
            plannedQty: 600,
            currentLedgerQty: 0,
            outboundSameHuQty: 600,
            outboundDocRef: "OUT-2026-000165");
        harness.SeedLedgerEntry(153, TestItemId, TestLocationId, 600, "HU-0000439");
        harness.SeedLedgerEntry(192, TestItemId, TestLocationId, 600, "HU-0000439");
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var draft = service.CreateReverseBackfillDraft([TestPalletId], "Сторно ошибочного backfill");
        Assert.True(draft.Success, draft.Message);
        Assert.Equal(1, draft.LineCount);

        var line = Assert.Single(harness.Store.GetDocLines(draft.DocId!.Value));
        Assert.Equal(DocType.InventoryCorrection, harness.GetDoc(draft.DocId!.Value).Type);
        Assert.Equal(-600, line.Qty, 3);
        Assert.Equal("HU-0000439", line.ToHu);

        var close = harness.CreateService().TryCloseDoc(draft.DocId!.Value, allowNegative: false);
        Assert.True(close.Success, string.Join("; ", close.Errors));
        Assert.Equal(0, harness.Store.GetLedgerBalance(TestItemId, TestLocationId, "HU-0000439"), 3);

        var secondDraft = service.CreateReverseBackfillDraft([TestPalletId], null);
        Assert.False(secondDraft.Success);
        Assert.Equal("NO_REVERSE_LINES", secondDraft.Error);
    }

    [Fact]
    public void ReverseBackfillDraft_MixedPallet_CreatesOneLinePerComponent()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 10, Name = "A", IsActive = true, BaseUom = "шт" });
        harness.SeedItem(new Item { Id = 20, Name = "B", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад" });
        harness.SeedOrder(new Order
        {
            Id = TestOrderId,
            OrderRef = "ORD-MIX",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedDoc(new Doc
        {
            Id = 200,
            DocRef = "PRD-MIX",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = TestOrderId,
            CreatedAt = DateTime.UtcNow
        });
        const long mixedPalletId = 451;
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = mixedPalletId,
            PrdDocId = 200,
            DocLineId = 2001,
            OrderId = TestOrderId,
            ItemId = 10,
            ItemName = "Микс",
            HuCode = "HU-0000451",
            PlannedQty = 1000,
            ToLocationId = TestLocationId,
            Status = ProductionPalletStatus.Filled,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 1,
                    ProductionPalletId = mixedPalletId,
                    DocLineId = 2001,
                    ItemId = 10,
                    ItemName = "A",
                    PlannedQty = 400
                },
                new ProductionPalletComponentLine
                {
                    Id = 2,
                    ProductionPalletId = mixedPalletId,
                    DocLineId = 2002,
                    ItemId = 20,
                    ItemName = "B",
                    PlannedQty = 600
                }
            ]
        });
        harness.SeedLedgerEntry(200, 10, TestLocationId, 400, "HU-0000451");
        harness.SeedLedgerEntry(200, 20, TestLocationId, 600, "HU-0000451");
        harness.SeedClosedOutbound(176, "OUT-2026-000176", TestOrderId, 10, TestLocationId, 400, "HU-0000451");
        harness.SeedClosedOutbound(176, "OUT-2026-000176", TestOrderId, 20, TestLocationId, 600, "HU-0000451");
        harness.SeedLedgerEntry(192, 10, TestLocationId, 400, "HU-0000451");
        harness.SeedLedgerEntry(192, 20, TestLocationId, 600, "HU-0000451");

        var service = new ProductionPalletFilledStockBackfillService(harness.Store);
        var draft = service.CreateReverseBackfillDraft([mixedPalletId], null);
        Assert.True(draft.Success);
        Assert.Equal(2, draft.LineCount);
        var lines = harness.Store.GetDocLines(draft.DocId!.Value);
        Assert.Contains(lines, line => line.ItemId == 10 && Math.Abs(line.Qty + 400) < 0.001);
        Assert.Contains(lines, line => line.ItemId == 20 && Math.Abs(line.Qty + 600) < 0.001);
    }

    [Fact]
    public void BackfillFilledStock_DryRun_DoesNotWriteLedger()
    {
        var harness = CreateFilledPalletHarness(
            huCode: "HU-0000437",
            plannedQty: 840,
            currentLedgerQty: 0,
            orderStatus: OrderStatus.InProgress);
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var dryRun = service.BackfillFilledStock(dryRun: true);
        Assert.True(dryRun.DryRun);
        Assert.Single(dryRun.Applied);
        Assert.Empty(harness.LedgerEntries.Where(entry => entry.DocId == 153));
    }

    private static CloseDocumentHarness CreateFilledPalletHarness(
        string huCode,
        double plannedQty,
        double currentLedgerQty,
        OrderStatus orderStatus = OrderStatus.InProgress,
        double outboundSameHuQty = 0,
        string? outboundDocRef = null,
        double outboundByOrderItemQty = 0)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = TestItemId, Name = "Товар", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад ГП" });
        harness.SeedOrder(new Order
        {
            Id = TestOrderId,
            OrderRef = "ORD-100",
            Type = OrderType.Customer,
            Status = orderStatus,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedDoc(new Doc
        {
            Id = 153,
            DocRef = "PRD-2026-000153",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = TestOrderId,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = TestPalletId,
            PrdDocId = 153,
            DocLineId = 15301,
            OrderId = TestOrderId,
            ItemId = TestItemId,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = plannedQty,
            ToLocationId = TestLocationId,
            ToLocationCode = "001",
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 10, 0, 0),
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });

        if (outboundSameHuQty > StockQuantityRules.QtyTolerance)
        {
            harness.SeedClosedOutbound(
                docId: 165,
                docRef: outboundDocRef ?? "OUT-2026-000165",
                orderId: TestOrderId,
                itemId: TestItemId,
                locationId: TestLocationId,
                qty: outboundSameHuQty,
                huCode: huCode);
        }

        if (outboundByOrderItemQty > StockQuantityRules.QtyTolerance)
        {
            harness.SeedClosedOutbound(
                docId: 166,
                docRef: "OUT-ORDER",
                orderId: TestOrderId,
                itemId: TestItemId,
                locationId: TestLocationId,
                qty: outboundByOrderItemQty,
                huCode: huCode);
        }

        if (!StockQuantityRules.IsEffectivelyZero(currentLedgerQty))
        {
            harness.SeedLedgerEntry(
                docId: 192,
                itemId: TestItemId,
                locationId: TestLocationId,
                qtyDelta: currentLedgerQty,
                huCode: huCode);
        }

        return harness;
    }
}
