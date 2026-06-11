using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedOrderLineHuFateStore
{
    IReadOnlyList<ScopedOrderLineHuFateCandidate> GetScopedOrderLineHuFateCandidates(
        IReadOnlyCollection<ScopedOrderLineHuFateKey> keys);
}
