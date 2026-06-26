using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IMarkingCutoverPreflightStore
{
    IReadOnlyList<MarkingCutoverPreflightEntry> GetMarkingCutoverPreflightEntries();
}
