using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class HuBalanceCorrectionTests
{
    private const long TestItemId = 9001;
    private const long TestLocationId = 9001;

    [Fact]
    public void CreateCorrectionDraft_BuildsOppositeSignLines_WhenTotalIsZero()
    {
        var harness = CreatePhantomHuScenario();
        var huRows = harness.Store.GetHuStockRows()
            .Where(row => row.ItemId == TestItemId && row.LocationId == TestLocationId)
            .ToList();
        Assert.Equal(4, huRows.Count);

        var service = new HuBalanceCorrectionService(harness.Store);

        var result = service.CreateCorrectionDraft(new HuBalanceCorrectionDraftRequest
        {
            ItemId = TestItemId,
            LocationId = TestLocationId,
            Comment = "Сторно HU-разбалансировки после OUT-2026-000164"
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, result.LineCount);

        var lines = harness.Store.GetDocLines(result.DocId!.Value);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, line => line.ToHu == "HU-A" && Math.Abs(line.Qty + 840) < 0.001);
        Assert.Contains(lines, line => line.ToHu == "HU-B" && Math.Abs(line.Qty + 840) < 0.001);
        Assert.Contains(lines, line => line.ToHu == "HU-C" && Math.Abs(line.Qty - 840) < 0.001);
        Assert.Contains(lines, line => line.ToHu == "HU-D" && Math.Abs(line.Qty - 840) < 0.001);

        var doc = harness.GetDoc(result.DocId!.Value);
        Assert.Equal(DocType.InventoryCorrection, doc.Type);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Contains("HU balances:", doc.Comment);
    }

    [Fact]
    public void CloseCorrection_ZeroesAllHuBalances_AndRepeatedCloseIsIdempotent()
    {
        var harness = CreatePhantomHuScenario();
        var draft = new HuBalanceCorrectionService(harness.Store).CreateCorrectionDraft(
            new HuBalanceCorrectionDraftRequest
            {
                ItemId = TestItemId,
                LocationId = TestLocationId
            });
        Assert.True(draft.Success);

        var docId = draft.DocId!.Value;
        var close = harness.CreateService().TryCloseDoc(docId, allowNegative: false);
        Assert.True(close.Success, string.Join("; ", close.Errors));

        foreach (var hu in new[] { "HU-A", "HU-B", "HU-C", "HU-D" })
        {
            Assert.Equal(0, harness.Store.GetLedgerBalance(TestItemId, TestLocationId, hu), 3);
        }

        Assert.Equal(0, SumHuBalances(harness, TestItemId, TestLocationId), 3);

        var secondClose = harness.CreateService().TryCloseDoc(docId, allowNegative: false);
        Assert.False(secondClose.Success);
        Assert.Contains(secondClose.Errors, error => error.Contains("уже закрыт", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, harness.LedgerEntries.Count(entry => entry.DocId == docId));
    }

    [Fact]
    public void CreateCorrectionDraft_RejectsWhenItemLocationTotalIsNotZero()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = TestItemId, Name = "Товар 64", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад ГП" });
        harness.SeedBalance(TestItemId, TestLocationId, 840, "HU-A");

        var result = new HuBalanceCorrectionService(harness.Store).CreateCorrectionDraft(
            new HuBalanceCorrectionDraftRequest
            {
                ItemId = TestItemId,
                LocationId = TestLocationId
            });

        Assert.False(result.Success);
        Assert.Equal("ITEM_LOCATION_TOTAL_NOT_ZERO", result.Error);
    }

    [Fact]
    public void CreateCorrectionDraft_RejectsWhenOnlyPositiveHuBalancesExist()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = TestItemId, Name = "Товар 64", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад ГП" });
        harness.SeedBalance(TestItemId, TestLocationId, 840, "HU-A");
        harness.SeedBalance(TestItemId, TestLocationId, 840, "HU-B");

        var result = new HuBalanceCorrectionService(harness.Store).CreateCorrectionDraft(
            new HuBalanceCorrectionDraftRequest
            {
                ItemId = TestItemId,
                LocationId = TestLocationId
            });

        Assert.False(result.Success);
        Assert.Equal("ITEM_LOCATION_TOTAL_NOT_ZERO", result.Error);
    }

    [Fact]
    public void CreateCorrectionDraft_AllowsTwoHuOppositeSigns_WhenTotalIsZero()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = TestItemId, Name = "Товар 64", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад ГП" });
        harness.SeedBalance(TestItemId, TestLocationId, 840, "HU-A");
        harness.SeedBalance(TestItemId, TestLocationId, -840, "HU-B");

        var result = new HuBalanceCorrectionService(harness.Store).CreateCorrectionDraft(
            new HuBalanceCorrectionDraftRequest
            {
                ItemId = TestItemId,
                LocationId = TestLocationId
            });

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.LineCount);
    }

    private static CloseDocumentHarness CreatePhantomHuScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = TestItemId, Name = "Хрен Столовый - Налив", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = TestLocationId, Code = "001", Name = "Склад ГП" });
        harness.SeedBalance(TestItemId, TestLocationId, 840, "HU-A");
        harness.SeedBalance(TestItemId, TestLocationId, 840, "HU-B");
        harness.SeedBalance(TestItemId, TestLocationId, -840, "HU-C");
        harness.SeedBalance(TestItemId, TestLocationId, -840, "HU-D");
        return harness;
    }

    private static double SumHuBalances(CloseDocumentHarness harness, long itemId, long locationId)
    {
        return harness.Store.GetHuStockRows()
            .Where(row => row.ItemId == itemId && row.LocationId == locationId)
            .Sum(row => row.Qty);
    }
}
