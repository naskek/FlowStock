using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class AutoDistributionTests
{
    [Fact]
    public void AutoDistributeProductionReceipt_MixesSharedHuLinesIntoOneHu()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000010",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Горчица",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 101,
            Name = "Аджика",
            MaxQtyPerHu = 378
        });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            ItemId = 100,
            Qty = 50,
            ToLocationId = 10,
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = 12,
            DocId = 1,
            ItemId = 101,
            Qty = 100,
            ToLocationId = 10,
            PackSingleHu = true
        });

        var service = harness.CreateService();
        var usedHuCount = service.AutoDistributeProductionReceiptHus(1);
        var lines = harness.GetDocLines(1);

        Assert.Equal(1, usedHuCount);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line.ToHu)));
        Assert.Equal(lines[0].ToHu, lines[1].ToHu, ignoreCase: true);
    }

    [Fact]
    public void AutoDistributeProductionReceipt_SplitLinesReceiveRealHuAndPreserveTotalQty()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 2,
            DocRef = "PRD-2026-000020",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 13, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Кетчуп",
            MaxQtyPerHu = 600
        });
        harness.SeedLocation(new Location { Id = 20, Code = "02", Name = "Склад 02" });
        harness.SeedLine(new DocLine
        {
            Id = 21,
            DocId = 2,
            ItemId = 200,
            Qty = 1300,
            ToLocationId = 20
        });

        var service = harness.CreateService();
        var usedHuCount = service.AutoDistributeProductionReceiptHus(2);
        var lines = harness.GetDocLines(2);

        Assert.True(usedHuCount >= 3);
        Assert.True(lines.Count >= 3);
        Assert.Equal(1300, lines.Sum(line => line.Qty), 6);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line.ToHu)));
    }
}
