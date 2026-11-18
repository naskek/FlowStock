namespace FlowStock.Core.Abstractions;

public interface IProductionReceiptMarkingStore
{
    void LockProductionReceiptMarkingLines();
    // Must run inside the caller's Close transaction; failure aborts all effects.
    void AssignProductionReceiptMarking(long docId, DateTime appliedAt);
}
