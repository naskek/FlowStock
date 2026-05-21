using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedHuReservationCandidatesStore
{
    IReadOnlyList<HuReservationCandidateSourceRow> GetHuReservationCandidateSources(
        long? customerOrderId,
        IReadOnlyCollection<long> itemIds,
        IReadOnlyCollection<string> excludeHuCodes);
}
