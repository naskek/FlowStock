using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class CanonicalCloseIntegrationTests
{
    [Fact]
    public void SuccessfulClose_ChangesDocStatusToClosed()
    {
        var harness = CreateInboundHarness(qty: 12);
        var service = harness.CreateService();

        var result = service.TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);

        var doc = harness.GetDoc(1);
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.NotNull(doc.ClosedAt);
    }

    [Fact]
    public void SuccessfulClose_WritesLedgerExactlyOnce()
    {
        var harness = CreateInboundHarness(qty: 12);
        var service = harness.CreateService();

        var result = service.TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);

        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal(1, entry.DocId);
        Assert.Equal(100, entry.ItemId);
        Assert.Equal(10, entry.LocationId);
        Assert.Equal(12, entry.QtyDelta);
        Assert.Null(entry.HuCode);
    }

    [Fact]
    public void InventoryClose_WritesDeltaAgainstCurrentBalance()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 2,
            DocRef = "INV-2026-000001",
            Type = DocType.Inventory,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedBalance(itemId: 100, locationId: 10, qty: 7);
        harness.SeedLine(new DocLine
        {
            Id = 20,
            DocId = 2,
            ItemId = 100,
            Qty = 12,
            ToLocationId = 10
        });

        var service = harness.CreateService();
        var result = service.TryCloseDoc(2, allowNegative: false);

        Assert.True(result.Success);

        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal(5, entry.QtyDelta);

        var doc = harness.GetDoc(2);
        Assert.Equal(DocStatus.Closed, doc.Status);
    }

    private static CloseDocumentHarness CreateInboundHarness(double qty)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "IN-2026-000001",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            ItemId = 100,
            Qty = qty,
            ToLocationId = 10
        });
        return harness;
    }
}
