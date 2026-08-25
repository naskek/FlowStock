using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IMarkingCutoverPreflightStore
{
    IReadOnlyList<MarkingCutoverPreflightEntry> GetMarkingCutoverPreflightEntries();
    IReadOnlyList<MarkingLegacyCutoverLineSnapshot> GetMarkingLegacyCutoverLineSnapshots() => [];
    IReadOnlyList<MarkingLegacyCutoverSubjectSnapshot> GetMarkingLegacyCutoverSubjectSnapshots() => [];
    void EnforceMarkingCutover(string expectedPreflightHash, string approvedBy, DateTime enforcedAt);
}
