using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class ValidationTests
{
    [Fact]
    public void CloseWithNoLines_Fails()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "IN-2026-000002",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(1, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Добавьте хотя бы один товар", StringComparison.Ordinal));
        Assert.Empty(result.Warnings);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(1).Status);
    }

    [Fact]
    public void WriteOffWithoutReason_Fails()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 2,
            DocRef = "WO-2026-000001",
            Type = DocType.WriteOff,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedBalance(itemId: 100, locationId: 10, qty: 50);
        harness.SeedLine(new DocLine
        {
            Id = 21,
            DocId = 2,
            ItemId = 100,
            Qty = 5,
            FromLocationId = 10
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(2, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("требуется причина", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(2).Status);
    }

    [Fact]
    public void ProductionReceiptWithoutHu_Fails()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 3,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 31,
            DocId = 3,
            ItemId = 100,
            Qty = 5,
            ToLocationId = 10
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(3, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("требуется HU", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ProductionReceiptExceedingMaxQtyPerHu_Fails()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 4,
            DocRef = "PRD-2026-000002",
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
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 41,
            DocId = 4,
            ItemId = 100,
            Qty = 601,
            ToLocationId = 10,
            ToHu = "HU-000001"
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(4, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("превышает лимит", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(4).Status);
    }

    [Fact]
    public void ProductionReceiptSharedHuWithinCapacity_Succeeds()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 5,
            DocRef = "PRD-2026-000003",
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
            Id = 51,
            DocId = 5,
            ItemId = 100,
            Qty = 50,
            ToLocationId = 10,
            ToHu = "HU-000001",
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = 52,
            DocId = 5,
            ItemId = 101,
            Qty = 100,
            ToLocationId = 10,
            ToHu = "HU-000001",
            PackSingleHu = true
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(5, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, harness.LedgerEntries.Count);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(5).Status);
    }

    [Fact]
    public void ProductionReceiptSharedHuOverCapacity_Fails()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 6,
            DocRef = "PRD-2026-000004",
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
            Id = 61,
            DocId = 6,
            ItemId = 100,
            Qty = 300,
            ToLocationId = 10,
            ToHu = "HU-000001",
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = 62,
            DocId = 6,
            ItemId = 101,
            Qty = 250,
            ToLocationId = 10,
            ToHu = "HU-000001",
            PackSingleHu = true
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(6, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("суммарная загрузка", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(6).Status);
    }
}
