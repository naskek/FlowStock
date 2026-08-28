namespace FlowStock.Core.Models.Marking;

public sealed record MarkingImportUploadFile(string FileName, byte[] Content);

public sealed record RelatedMarkingRequest(
    Guid MarkingOrderId,
    string RequestNumber,
    string Gtin,
    int RequiredQuantity,
    int ReserveQuantity,
    int RequestedQuantity,
    int ImportedQuantity,
    string ScopeSnapshotHash,
    int ActiveScopedQuantity = -1,
    DateTime CreatedAt = default)
{
    public int EffectiveActiveScopedQuantity => ActiveScopedQuantity < 0 ? RequiredQuantity : ActiveScopedQuantity;
    public int OperationalDeficit => Math.Max(0, EffectiveActiveScopedQuantity - ImportedQuantity);
    public int RemainingRequestedCapacity => Math.Max(0, RequestedQuantity - ImportedQuantity);
}

public sealed record OrderScopedMarkingImportFilePreview(
    string FileName,
    string FileHash,
    long FileSizeBytes,
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    int DuplicateRows,
    IReadOnlyList<string> Warnings);

public sealed record OrderScopedMarkingImportRequestPreview(
    Guid MarkingOrderId,
    string RequestNumber,
    string Gtin,
    int RequiredQuantity,
    int ReserveQuantity,
    int RequestedQuantity,
    int ImportedBefore,
    int ValidInBatch,
    int ImportedAfter,
    bool CoverageWillActivate,
    bool ReserveShort,
    string ScopeSnapshotHash,
    int OperationalRequiredQuantity = 0);

public sealed record OrderScopedMarkingImportPreviewResult(
    bool IsValid,
    string? ErrorCode,
    string Message,
    string SnapshotHash,
    bool RequiresRecoveryConfirmation,
    IReadOnlyList<OrderScopedMarkingImportFilePreview> Files,
    IReadOnlyList<OrderScopedMarkingImportRequestPreview> Requests,
    IReadOnlyList<string> Warnings);

public sealed record OrderScopedMarkingImportConfirmCommand(
    long RelatedOrderId,
    Guid BatchId,
    string SnapshotHash,
    string IdempotencyKey,
    bool ConfirmRecovery,
    IReadOnlyList<MarkingImportUploadFile> Files,
    IReadOnlyList<OrderScopedMarkingImportCode> Codes,
    IReadOnlyList<OrderScopedMarkingImportRequestPreview> Requests,
    DateTime ConfirmedAt);

public sealed record OrderScopedMarkingImportCode(
    Guid MarkingOrderId,
    string Gtin,
    string Code,
    string CodeHash,
    string FileHash,
    int SourceRowNumber);

public sealed record OrderScopedMarkingImportConfirmResult(
    Guid BatchId,
    bool WasAlreadyConfirmed,
    int PersistedCodeCount,
    IReadOnlyList<Guid> ActivatedMarkingOrderIds);
