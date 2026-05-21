using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedOperationOrderCandidatesStore
{
    IReadOnlyList<Order> GetOperationOrderCandidates(DocType docType, string? query, int limit);
}
