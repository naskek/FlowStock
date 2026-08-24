using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IMarkingCutoverPreflightStore
{
    IReadOnlyList<MarkingCutoverPreflightEntry> GetMarkingCutoverPreflightEntries();
    void EnforceMarkingCutover(string expectedPreflightHash, string approvedBy, DateTime enforcedAt);
}
