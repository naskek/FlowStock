using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletFilledStockBackfillTests
{
    private const long TestItemId = 64;
    private const long TestLocationId = 1;

    [Fact]
    public void FilledWithoutStockDiagnostics_ReturnsMissingQty_WhenLedgerIsZero()
    {
        var harness = CreateFilledPalletHarness(ledgerQty: 0);
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var gaps = service.GetFilledWithoutStock();

        var gap = Assert.Single(gaps);
        Assert.Equal("HU-000437", gap.HuCode);
        Assert.Equal(840, gap.PlannedQty);
        Assert.Equal(0, gap.LedgerQty);
        Assert.Equal(840, gap.MissingQty);
        Assert.Equal("PRD-2026-000153", gap.PrdDocRef);
    }

    [Fact]
    public void BackfillFilledStock_CreatesLedgerOnce_AndSecondRunIsNoOp()
    {
        var harness = CreateFilledPalletHarness(ledgerQty: 0);
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var apply = service.BackfillFilledStock(dryRun: false);
        Assert.False(apply.DryRun);
        Assert.Single(apply.Applied);
        Assert.Equal(1, apply.LedgerRowsWritten);

        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal(153, entry.DocId);
        Assert.Equal(TestItemId, entry.ItemId);
        Assert.Equal(TestLocationId, entry.LocationId);
        Assert.Equal("HU-000437", entry.HuCode);
        Assert.Equal(840, entry.QtyDelta);

        Assert.Equal(840, harness.Store.GetLedgerBalance(TestItemId, TestLocationId, "HU-000437"), 3);
        Assert.Empty(service.GetFilledWithoutStock());

        var secondApply = service.BackfillFilledStock(dryRun: false);
        Assert.Empty(secondApply.Applied);
        Assert.Equal(0, secondApply.LedgerRowsWritten);
        Assert.Single(harness.LedgerEntries);
    }

    [Fact]
    public void BackfillFilledStock_IgnoresPendingPallet()
    {
        var harness = CreateFilledPalletHarness(ledgerQty: 0);
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 438,
            PrdDocId = 153,
            DocLineId = 15302,
            ItemId = TestItemId,
            ItemName = "Товар",
            HuCode = "HU-000438",
            PlannedQty = 840,
            ToLocationId = TestLocationId,
            ToLocationCode = "001",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedBalance(TestItemId, TestLocationId, 0, "HU-000438");

        var gaps = new ProductionPalletFilledStockBackfillService(harness.Store).GetFilledWithoutStock();

        Assert.Single(gaps);
        Assert.Equal("HU-000437", gaps[0].HuCode);
    }

    [Fact]
    public void BackfillFilledStock_DryRun_DoesNotWriteLedger()
    {
        var harness = CreateFilledPalletHarness(ledgerQty: 0);
        var service = new ProductionPalletFilledStockBackfillService(harness.Store);

        var dryRun = service.BackfillFilledStock(dryRun: true);

        Assert.True(dryRun.DryRun);
        Assert.Single(dryRun.Gaps);
        Assert.Empty(harness.LedgerEntries);
    }

    private static CloseDocumentHarness CreateFilledPalletHarness(double ledgerQty)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = TestItemId, Name = "Товар 64", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад ГП" });
        harness.SeedDoc(new Doc
        {
            Id = 153,
            DocRef = "PRD-2026-000153",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 437,
            PrdDocId = 153,
            DocLineId = 15301,
            ItemId = TestItemId,
            ItemName = "Товар 64",
            HuCode = "HU-000437",
            PlannedQty = 840,
            ToLocationId = TestLocationId,
            ToLocationCode = "001",
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 10, 0, 0),
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        if (!StockQuantityRules.IsEffectivelyZero(ledgerQty))
        {
            harness.SeedBalance(TestItemId, TestLocationId, ledgerQty, "HU-000437");
        }

        return harness;
    }
}
