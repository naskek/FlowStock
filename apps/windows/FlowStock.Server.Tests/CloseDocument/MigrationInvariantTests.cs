using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class MigrationInvariantTests
{
    [Fact]
    public void KmRemainsDisabled_ForMarkedProductionReceipt()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000003",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Горчица",
            IsMarked = true
        });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            ItemId = 100,
            Qty = 5,
            ToLocationId = 10,
            ToHu = "HU-000001"
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(1).Status);
    }

    [Fact]
    public void WarningsRemainEmpty_UnderMigrationContract()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 2,
            DocRef = "IN-2026-000004",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc)
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(2, allowNegative: false);

        Assert.Empty(result.Warnings);
    }
}
