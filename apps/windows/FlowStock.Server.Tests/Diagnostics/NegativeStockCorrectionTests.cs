using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class NegativeStockCorrectionTests
{
    [Fact]
    public void GetNegativeStockBalances_ReturnsNegativeHuRow()
    {
        var harness = CreateScenarioWithNegativeHu(-1200);
        var rows = harness.Store.GetNegativeStockBalances();
        var row = Assert.Single(rows);
        Assert.Equal(-1200, row.Qty);
        Assert.Equal("HU-NEG-001", row.HuCode);
    }

    [Fact]
    public void GetHuStockRows_DoesNotReturnEffectivelyZeroHu()
    {
        var harness = CreateScenarioWithNegativeHu(-1200);
        harness.CreateService().TryCloseDoc(CreateAndCloseCorrection(harness, 1200), allowNegative: false);

        var huRows = harness.Store.GetHuStockRows();
        Assert.DoesNotContain(huRows, row => string.Equals(row.HuCode, "HU-NEG-001", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateCorrectionDraft_AddsExpectedLine()
    {
        var harness = CreateScenarioWithNegativeHu(-1200);
        var service = new NegativeStockCorrectionService(harness.Store);

        var result = service.CreateCorrectionDraft(new NegativeStockCorrectionDraftRequest
        {
            ItemId = 100,
            LocationId = 10,
            HuCode = "HU-NEG-001",
            QtyToCompensate = 1200,
            Comment = "Сторно ошибочного отрицательного остатка"
        });

        Assert.True(result.Success);
        var doc = harness.GetDoc(result.DocId!.Value);
        Assert.Equal(DocType.InventoryCorrection, doc.Type);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Contains("Корректировка отрицательного остатка", doc.Comment);
        var lines = harness.Store.GetDocLines(doc.Id);
        var line = Assert.Single(lines);
        Assert.Equal(100, line.ItemId);
        Assert.Equal(10, line.ToLocationId);
        Assert.Equal("HU-NEG-001", line.ToHu);
        Assert.Equal(1200, line.Qty);
    }

    [Fact]
    public void CloseCorrection_CompensatesNegativeBalance_AndRepeatedCloseIsIdempotent()
    {
        var harness = CreateScenarioWithNegativeHu(-1200);
        var docId = CreateAndCloseCorrection(harness, 1200);

        Assert.Equal(0, harness.Store.GetLedgerBalance(100, 10, "HU-NEG-001"), 3);
        Assert.Single(harness.LedgerEntries.Where(entry => entry.QtyDelta > 0));

        var docs = harness.CreateService();
        var secondClose = docs.TryCloseDoc(docId, allowNegative: false);
        Assert.False(secondClose.Success);
        Assert.Contains(secondClose.Errors, error => error.Contains("уже закрыт", StringComparison.OrdinalIgnoreCase));
        Assert.Single(harness.LedgerEntries.Where(entry => entry.QtyDelta > 0));
    }

    [Fact]
    public void CreateCorrectionDraft_RejectsQtyAboveNegativeBalance()
    {
        var harness = CreateScenarioWithNegativeHu(-500);
        var service = new NegativeStockCorrectionService(harness.Store);

        var result = service.CreateCorrectionDraft(new NegativeStockCorrectionDraftRequest
        {
            ItemId = 100,
            LocationId = 10,
            HuCode = "HU-NEG-001",
            QtyToCompensate = 501
        });

        Assert.False(result.Success);
        Assert.Equal("EXCEEDS_NEGATIVE", result.Error);
    }

    private static CloseDocumentHarness CreateScenarioWithNegativeHu(double negativeQty)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 100, Name = "Товар 100", IsActive = true, BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = 10, Code = "A-01", Name = "A-01" });
        harness.SeedBalance(100, 10, negativeQty, "HU-NEG-001");
        return harness;
    }

    private static long CreateAndCloseCorrection(CloseDocumentHarness harness, double qty)
    {
        var draft = new NegativeStockCorrectionService(harness.Store).CreateCorrectionDraft(
            new NegativeStockCorrectionDraftRequest
            {
                ItemId = 100,
                LocationId = 10,
                HuCode = "HU-NEG-001",
                QtyToCompensate = qty
            });
        Assert.True(draft.Success);
        var close = harness.CreateService().TryCloseDoc(draft.DocId!.Value, allowNegative: false);
        Assert.True(close.Success, string.Join("; ", close.Errors));
        return draft.DocId!.Value;
    }
}
