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

    [Fact]
    public void OutboundWithoutLineHu_AllocatesFromMultipleHusInLocation()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 3,
            DocRef = "OUT-2026-000005",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc),
            PartnerId = 20
        });
        harness.SeedPartner(new Partner { Id = 20, Code = "C001", Name = "Покупатель" });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedBalance(100, 10, 4, "HU-000001");
        harness.SeedBalance(100, 10, 6, "HU-000002");
        harness.SeedLine(new DocLine
        {
            Id = 12,
            DocId = 3,
            ItemId = 100,
            Qty = 10,
            FromLocationId = 10
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(3, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(3).Status);
        Assert.Contains(harness.LedgerEntries, entry => entry.HuCode == "HU-000001" && Math.Abs(entry.QtyDelta + 4) < 0.000001);
        Assert.Contains(harness.LedgerEntries, entry => entry.HuCode == "HU-000002" && Math.Abs(entry.QtyDelta + 6) < 0.000001);
    }

    [Fact]
    public void WriteOffWithoutLineHu_AllocatesFromMultipleHusInLocation()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 4,
            DocRef = "WO-2026-000006",
            Type = DocType.WriteOff,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc),
            ReasonCode = "DAMAGED"
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedBalance(100, 10, 4, "HU-000001");
        harness.SeedBalance(100, 10, 6, "HU-000002");
        harness.SeedLine(new DocLine
        {
            Id = 14,
            DocId = 4,
            ItemId = 100,
            Qty = 10,
            FromLocationId = 10
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(4, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(4).Status);
        Assert.Contains(harness.LedgerEntries, entry => entry.HuCode == "HU-000001" && Math.Abs(entry.QtyDelta + 4) < 0.000001);
        Assert.Contains(harness.LedgerEntries, entry => entry.HuCode == "HU-000002" && Math.Abs(entry.QtyDelta + 6) < 0.000001);
    }

    [Fact]
    public void WriteOffWithLineHu_UsesLineHuBalance()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 5,
            DocRef = "WO-2026-000007",
            Type = DocType.WriteOff,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc),
            ReasonCode = "DAMAGED"
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedBalance(100, 10, 6, "HU-000001");
        harness.SeedBalance(100, 10, 4, "HU-000002");
        harness.SeedLine(new DocLine
        {
            Id = 15,
            DocId = 5,
            ItemId = 100,
            Qty = 4,
            FromLocationId = 10,
            FromHu = "HU-000002"
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(5, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(5).Status);
        Assert.Single(harness.LedgerEntries);
        Assert.Contains(harness.LedgerEntries, entry => entry.HuCode == "HU-000002" && Math.Abs(entry.QtyDelta + 4) < 0.000001);
    }

    [Fact]
    public void WriteOffWithLineHu_RejectsWhenLineHuShort()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 6,
            DocRef = "WO-2026-000008",
            Type = DocType.WriteOff,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc),
            ReasonCode = "DAMAGED"
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedBalance(100, 10, 600, "HU-000001");
        harness.SeedBalance(100, 10, 300, "HU-000002");
        harness.SeedLine(new DocLine
        {
            Id = 16,
            DocId = 6,
            ItemId = 100,
            Qty = 900,
            FromLocationId = 10,
            FromHu = "HU-000001"
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(6, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("HU HU-000001", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(6).Status);
    }
}
