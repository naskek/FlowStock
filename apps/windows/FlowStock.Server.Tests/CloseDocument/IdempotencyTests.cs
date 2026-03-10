using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class IdempotencyTests
{
    [Fact]
    public void RepeatedClose_DoesNotDuplicateLedger()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "IN-2026-000003",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 13, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            ItemId = 100,
            Qty = 12,
            ToLocationId = 10
        });

        var service = harness.CreateService();

        var first = service.TryCloseDoc(1, allowNegative: false);
        var second = service.TryCloseDoc(1, allowNegative: false);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains(second.Errors, error => error.Contains("уже закрыт", StringComparison.OrdinalIgnoreCase));
        Assert.Single(harness.LedgerEntries);
    }

    [Fact(Skip = "Target canonical ALREADY_CLOSED success/no-op semantics are defined in the contract but not implemented yet.")]
    public void AlreadyClosed_ReturnsCanonicalNoOpSemantics()
    {
    }
}
