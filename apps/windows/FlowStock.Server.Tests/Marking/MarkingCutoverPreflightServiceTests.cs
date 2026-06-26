using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingCutoverPreflightServiceTests
{
    [Fact]
    public void SameEntriesInDifferentInputOrder_ProduceSameCanonicalJsonAndHash()
    {
        var generatedAt = new DateTime(2026, 6, 26, 10, 0, 0, DateTimeKind.Utc);
        var first = Run(generatedAt, Entry(2, 20, "B"), Entry(1, 10, "A"));
        var second = Run(generatedAt, Entry(1, 10, "A"), Entry(2, 20, "B"));

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void GeneratedAt_DoesNotParticipateInHash()
    {
        var entries = new[] { Entry(1, 10, "A") };
        var first = Run(new DateTime(2026, 6, 26, 10, 0, 0, DateTimeKind.Utc), entries);
        var second = Run(new DateTime(2026, 6, 26, 11, 0, 0, DateTimeKind.Utc), entries);

        Assert.NotEqual(first.GeneratedAt, second.GeneratedAt);
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void ChangingIssueContent_ChangesHash()
    {
        var generatedAt = new DateTime(2026, 6, 26, 10, 0, 0, DateTimeKind.Utc);
        var first = Run(generatedAt, Entry(1, 10, "A", details: "old"));
        var second = Run(generatedAt, Entry(1, 10, "A", details: "new"));

        Assert.NotEqual(first.CanonicalJson, second.CanonicalJson);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void NullOrderAndLineValues_AreSortedDeterministicallyLast()
    {
        var result = Run(
            new DateTime(2026, 6, 26, 10, 0, 0, DateTimeKind.Utc),
            Entry(null, null, "C"),
            Entry(1, null, "B"),
            Entry(1, 10, "A"));

        Assert.Collection(
            result.Entries,
            entry =>
            {
                Assert.Equal(1, entry.OrderId);
                Assert.Equal(10, entry.OrderLineId);
                Assert.Equal("A", entry.IssueCode);
            },
            entry =>
            {
                Assert.Equal(1, entry.OrderId);
                Assert.Null(entry.OrderLineId);
                Assert.Equal("B", entry.IssueCode);
            },
            entry =>
            {
                Assert.Null(entry.OrderId);
                Assert.Null(entry.OrderLineId);
                Assert.Equal("C", entry.IssueCode);
            });
    }

    [Fact]
    public void SameMainKeysDifferentContent_ProduceSameCanonicalJsonAndHashInReverseInputOrder()
    {
        var generatedAt = new DateTime(2026, 6, 26, 10, 0, 0, DateTimeKind.Utc);
        var warning = new MarkingCutoverPreflightEntry(
            1,
            10,
            "MARKING_SHARED",
            "warning",
            3,
            1,
            2,
            "same-details",
            "approve");
        var error = new MarkingCutoverPreflightEntry(
            1,
            10,
            "MARKING_SHARED",
            "error",
            3,
            2,
            1,
            "same-details",
            "replace");

        var first = Run(generatedAt, warning, error);
        var second = Run(generatedAt, error, warning);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.Hash, second.Hash);
    }

    private static MarkingCutoverPreflightResult Run(DateTime generatedAt, params MarkingCutoverPreflightEntry[] entries)
    {
        return new MarkingCutoverPreflightService(new FakePreflightStore(entries)).Run(generatedAt);
    }

    private static MarkingCutoverPreflightEntry Entry(
        long? orderId,
        long? orderLineId,
        string issueCode,
        string details = "details")
    {
        return new MarkingCutoverPreflightEntry(
            orderId,
            orderLineId,
            issueCode,
            "error",
            null,
            null,
            null,
            details,
            "fix");
    }

    private sealed class FakePreflightStore : IMarkingCutoverPreflightStore
    {
        private readonly IReadOnlyList<MarkingCutoverPreflightEntry> _entries;

        public FakePreflightStore(IReadOnlyList<MarkingCutoverPreflightEntry> entries)
        {
            _entries = entries;
        }

        public IReadOnlyList<MarkingCutoverPreflightEntry> GetMarkingCutoverPreflightEntries() => _entries;
    }
}
