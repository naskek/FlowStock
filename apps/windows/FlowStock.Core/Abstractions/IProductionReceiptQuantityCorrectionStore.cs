namespace FlowStock.Core.Abstractions;

// Preview is advisory. Confirm must reread under the caller's correction locks.
public sealed record ReceiptQuantityCorrectionSlice(
    long AssignmentEventId, Guid PoolId, long ReceiptLineId, long Quantity, bool IsValid);

public interface IProductionReceiptQuantityCorrectionStore
{
    IReadOnlyList<ReceiptQuantityCorrectionSlice> ReadReceiptQuantityCorrection(long docId, bool lockData);
    void ReleaseReceiptQuantityCorrection(long adjustmentId, long sourceDocId, long corDocId);
}
