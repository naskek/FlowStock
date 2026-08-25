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
    public void FrozenShipmentSnapshot_ParticipatesInHashAndIsSortedDeterministically()
    {
        var first = new MarkingCutoverPreflightService(new FakePreflightStore([], [
            LegacyLine(2, 20, 2), LegacyLine(1, 10, 1)
        ])).Run(DateTime.UtcNow);
        var second = new MarkingCutoverPreflightService(new FakePreflightStore([], [
            LegacyLine(1, 10, 1), LegacyLine(2, 20, 2)
        ])).Run(DateTime.UtcNow.AddMinutes(1));
        var changed = new MarkingCutoverPreflightService(new FakePreflightStore([], [
            LegacyLine(1, 10, 0), LegacyLine(2, 20, 2)
        ])).Run(DateTime.UtcNow);

        Assert.Equal(first.Hash, second.Hash);
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.Equal([10L, 20L], first.LegacyLineSnapshots!.Select(row => row.OrderLineId));
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

    private static MarkingLegacyCutoverLineSnapshot LegacyLine(long orderId, long lineId, decimal shipped) =>
        new(orderId, lineId, "CUSTOMER", "FLOWSTOCK", 0, lineId, "04600000000000",
            10, shipped, 10 - shipped, 10 - shipped);

    private sealed class FakePreflightStore : IMarkingCutoverPreflightStore
    {
        private readonly IReadOnlyList<MarkingCutoverPreflightEntry> _entries;
        private readonly IReadOnlyList<MarkingLegacyCutoverLineSnapshot> _lines;

        public FakePreflightStore(
            IReadOnlyList<MarkingCutoverPreflightEntry> entries,
            IReadOnlyList<MarkingLegacyCutoverLineSnapshot>? lines = null)
        {
            _entries = entries;
            _lines = lines ?? [];
        }

        public IReadOnlyList<MarkingCutoverPreflightEntry> GetMarkingCutoverPreflightEntries() => _entries;
        public IReadOnlyList<MarkingLegacyCutoverLineSnapshot> GetMarkingLegacyCutoverLineSnapshots() => _lines;

        public void EnforceMarkingCutover(string expectedPreflightHash, string approvedBy, DateTime enforcedAt) =>
            throw new NotSupportedException();
    }
}
