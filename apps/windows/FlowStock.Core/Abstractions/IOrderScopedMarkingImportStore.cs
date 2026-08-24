using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Server-authoritative seam for resolving currently related immutable request scopes
/// and atomically confirming one multi-file import batch.
/// </summary>
public interface IOrderScopedMarkingImportStore
{
    IReadOnlyList<RelatedMarkingRequest> GetRelatedOutstandingMarkingRequests(long orderId);
    IReadOnlySet<string> FindExistingRealMarkingCodeHashes(IReadOnlyCollection<string> normalizedCodeHashes);
    OrderScopedMarkingImportConfirmResult? FindConfirmedOrderScopedMarkingImport(
        long relatedOrderId,
        Guid batchId,
        string idempotencyKey,
        string snapshotHash);
    OrderScopedMarkingImportConfirmResult ConfirmOrderScopedMarkingImport(OrderScopedMarkingImportConfirmCommand command);
}
