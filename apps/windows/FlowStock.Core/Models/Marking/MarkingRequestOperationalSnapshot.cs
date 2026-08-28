namespace FlowStock.Core.Models.Marking;

public static class MarkingRequestOperationalClassifications
{
    public const string ExactActive = "EXACT_ACTIVE";
    public const string PartiallyRetired = "PARTIALLY_RETIRED";
    public const string FullyRetired = "FULLY_RETIRED";
}

public sealed record MarkingRequestOperationalSnapshot(
    Guid MarkingOrderId,
    string RequestNumber,
    long? OriginalOrderId,
    long? CurrentOrderId,
    long? CurrentOrderLineId,
    long ItemId,
    string ItemName,
    string Gtin,
    int RequiredQuantity,
    int ReserveQuantity,
    int RequestedQuantity,
    int OriginalScopedQuantity,
    int ActiveScopedQuantity,
    int ImportedRealQuantity,
    int ActiveCoveredQuantity,
    DateTime CreatedAt,
    string Classification,
    string ScopeSnapshotHash)
{
    public int OperationalDeficit => Math.Max(0, ActiveScopedQuantity - ImportedRealQuantity);
    public int RemainingRequestedCapacity => Math.Max(0, RequestedQuantity - ImportedRealQuantity);
    public bool IsOperational => ActiveScopedQuantity > 0;
}

public sealed record MarkingRequestExportBatchSnapshot(
    Guid BatchId,
    long OrderId,
    string ExpectedSnapshotHash,
    string PostExportSnapshotHash,
    int ReserveQuantity,
    DateTime CreatedAt,
    IReadOnlyList<MarkingRequestExportBatchRequestSnapshot> Requests);

public sealed record MarkingRequestExportBatchRequestSnapshot(
    Guid MarkingOrderId,
    long ItemId,
    string ItemName,
    string Gtin,
    int RequiredQuantity,
    int ReserveQuantity,
    int RequestedQuantity);

public sealed record CreateMarkingRequestExportBatchCommand(
    Guid BatchId,
    long OrderId,
    string ExpectedSnapshotHash,
    string PostExportSnapshotHash,
    int ReserveQuantity,
    string Actor,
    DateTime CreatedAt,
    IReadOnlyList<MarkingRequestExportBatchRequestSnapshot> Requests);
