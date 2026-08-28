using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IMarkingLineProgressStore
{
    IReadOnlyDictionary<long, MarkingLineProgress> GetMarkingLineProgress(
        IReadOnlyCollection<long> orderIds);
}
