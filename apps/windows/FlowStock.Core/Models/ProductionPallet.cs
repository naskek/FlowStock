namespace FlowStock.Core.Models;

public static class ProductionPalletStatus
{
    public const string Planned = "PLANNED";
    public const string Printed = "PRINTED";
    public const string PartiallyFilled = "PARTIALLY_FILLED";
    public const string Filled = "FILLED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// Stable machine-readable error codes for production filling scan/fill results.
/// The code identifies the failure; a separate human-readable message is returned
/// alongside it. Never use a localized message string as a code.
/// </summary>
public static class ProductionFillingErrorCodes
{
    public const string HuRequired = "HU_REQUIRED";
    public const string PalletNotFound = "PALLET_NOT_FOUND";
    public const string PalletBelongsToAnotherOrder = "PALLET_BELONGS_TO_ANOTHER_ORDER";
    public const string PalletAlreadyFilled = "PALLET_ALREADY_FILLED";
    public const string PalletAlreadyFilledInOtherOrder = "PALLET_ALREADY_FILLED_IN_OTHER_ORDER";
    public const string PalletCancelled = "PALLET_CANCELLED";
    public const string PrdAlreadyClosed = "PRD_ALREADY_CLOSED";
    public const string PalletPlanInvalid = "PALLET_PLAN_INVALID";
    public const string FillExceedsRemaining = "FILL_EXCEEDS_REMAINING";
    public const string MixedComponentSelectionRequired = "MIXED_COMPONENT_SELECTION_REQUIRED";
}

public sealed class ProductionPallet
{
    public long Id { get; init; }
    public long PrdDocId { get; init; }
    public long DocLineId { get; init; }
    public long? OrderId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public long? ToLocationId { get; init; }
    public string? ToLocationCode { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Planned;
    public int PalletNo { get; init; }
    public int PalletCount { get; init; }
    public DateTime? PrintedAt { get; init; }
    public DateTime? FilledAt { get; init; }
    public string? FilledByDeviceId { get; init; }
    public string? CancelReason { get; init; }
    public DateTime? CancelledAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<ProductionPalletComponentLine> Lines { get; init; } = Array.Empty<ProductionPalletComponentLine>();
    public bool IsMixedPallet => Lines.Count > 1;
    public int FilledComponentCount => Lines.Count(line => line.IsCompleted);
    public int TotalComponentCount => Lines.Count;
    public bool HasComponentProgress => Lines.Any(line => line.FilledQty > StockQuantityRules.QtyTolerance);
    public bool AreAllComponentsFilled => Lines.Count > 0 && Lines.All(line => line.IsCompleted);
    public string EffectiveStatus =>
        string.Equals(Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
            ? ProductionPalletStatus.Filled
            : IsMixedPallet && HasComponentProgress && !AreAllComponentsFilled
                ? ProductionPalletStatus.PartiallyFilled
                : Status;
    public bool CanFill =>
        !AreAllComponentsFilled
        && (string.Equals(Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase));
}

public sealed class ProductionPalletComponentLine
{
    public long Id { get; init; }
    public long ProductionPalletId { get; init; }
    public long DocLineId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string Uom { get; init; } = "шт";
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public DateTime? FilledAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsCompleted => FilledQty + StockQuantityRules.QtyTolerance >= PlannedQty;
}

public sealed class ProductionPalletSummary
{
    public int PlannedPalletCount { get; init; }
    public double PlannedQty { get; init; }
    public int FilledPalletCount { get; init; }
    public double FilledQty { get; init; }
    public int RemainingPalletCount { get; init; }
    public double RemainingQty { get; init; }
}

public sealed class ProductionPalletLineSummary
{
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double OrderedQty { get; init; }
    public int PlannedPalletCount { get; init; }
    public double PlannedQty { get; init; }
    public int FilledPalletCount { get; init; }
    public double FilledQty { get; init; }
    public int RemainingPalletCount { get; init; }
    public double RemainingQty { get; init; }
}

public sealed class ProductionPalletDocument
{
    public long PrdDocId { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
    public IReadOnlyList<ProductionPalletLineSummary> Lines { get; init; } = Array.Empty<ProductionPalletLineSummary>();
    public IReadOnlyList<ProductionPallet> Pallets { get; init; } = Array.Empty<ProductionPallet>();
}

public sealed class ProductionPalletWorkItem
{
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public string PrdStatus { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
}

public sealed class ProductionFillingOrder
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderTypeDisplay { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string OrderStatusDisplay { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public long? PrdDocId { get; init; }
    public string? PrdDocRef { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
    public ProductionOperationProgress Progress { get; init; } = new();
}

public sealed class ProductionFillingContext
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderTypeDisplay { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string OrderStatusDisplay { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public ProductionPalletDocument Document { get; init; } = new();
    public ProductionOperationProgress Progress { get; init; } = new();
}

public sealed class ProductionOperationProgress
{
    public int RequiredPallets { get; init; }
    public int ScannedPallets { get; init; }
    public int RemainingPallets { get; init; }
    public bool CanClose { get; init; }
    public bool IsClosed { get; init; }
    public string OperationFingerprint { get; init; } = string.Empty;
}

public sealed class ProductionFillingCompleteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime? ClosedAt { get; init; }
    public ProductionFillingContext? Context { get; init; }
}

public sealed class ProductionPalletOrderPlanResult
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public bool WasExisting { get; init; }
    public bool ProductionRequired { get; init; } = true;
    public string Message { get; init; } = string.Empty;
    public ProductionPalletSummary Summary { get; init; } = new();
    public ProductionPalletDocument Document { get; init; } = new();

    /// <summary>
    /// Post-confirm preview fingerprint, computed inside the same confirm transaction
    /// (after PlanProductionPallets, before the orders lock is released). Empty for
    /// flows that do not recompute it (legacy PlanOrder).
    /// </summary>
    public string NewPreviewFingerprint { get; init; } = string.Empty;
}

public sealed class ProductionPalletPlanCleanupCounts
{
    public int RemovedPalletCount { get; init; }
    public int RemovedLineCount { get; init; }
    public IReadOnlyList<long> RemovedPalletIds { get; init; } = Array.Empty<long>();
}

public sealed class ProductionPalletCancelPlanResult
{
    public long OrderId { get; init; }
    public long PrdDocId { get; init; }
    public string Message { get; init; } = string.Empty;
    public int RemovedPalletCount { get; init; }
    public int RemovedLineCount { get; init; }
    public IReadOnlyList<long> RequestedPalletIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> RemovedPalletIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> SkippedPalletIds { get; init; } = Array.Empty<long>();
}

public sealed class ProductionPalletCancelPlanOptions
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPalletCancelPlanRow> Rows { get; init; } = Array.Empty<ProductionPalletCancelPlanRow>();
}

public sealed class ProductionPalletCancelPlanRow
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Planned;
    public bool IsSelectable { get; init; }
    public bool IsSelectedByDefault { get; init; }
    public string? DisabledReason { get; init; }
    public bool HasMarkingWarning { get; init; }
}

public sealed class ProductionPalletPlanAdoptionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long SourceOrderId { get; init; }
    public long TargetOrderId { get; init; }
    public long SourcePrdDocId { get; init; }
    public long TargetPrdDocId { get; init; }
    public int TransferredPalletCount { get; init; }
    public int TransferredLineCount { get; init; }
    public IReadOnlyList<string> TransferredHuCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProductionPalletPlanAdoptionWarning> Warnings { get; init; } = Array.Empty<ProductionPalletPlanAdoptionWarning>();
    public string? SourceOrderStatus { get; init; }
    public bool SourceOrderCommentUpdated { get; init; }
}

public sealed class ProductionPalletPlanAdoptionWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public static class ProductionPalletPrintSourceType
{
    public const string ProductionPallet = "production_pallet";
    public const string ReservedHu = "reserved_hu";
}

public sealed class ProductionPalletPrintRow
{
    public string SourceType { get; init; } = ProductionPalletPrintSourceType.ProductionPallet;
    public long PalletId { get; init; }
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdRef { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public string StorageConditions { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
    public int PalletNo { get; init; }
    public int PalletCount { get; init; }
    public string StoragePlace { get; init; } = string.Empty;
    public DateTime? ProductionDate { get; init; }
    public string Comment { get; init; } = string.Empty;
    public bool IsMixedPallet { get; init; }
    public string Composition { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPalletPrintLine> Lines { get; init; } = Array.Empty<ProductionPalletPrintLine>();
    public string Status { get; init; } = ProductionPalletStatus.Planned;
}

public sealed class ProductionPalletPrintLine
{
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
}

public sealed class ProductionPalletScanResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorMessage { get; init; }
    public bool AlreadyFilled { get; init; }
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long PalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemBrand { get; init; }
    public string BaseUom { get; init; } = "шт";
    public double PlannedQty { get; init; }
    public bool IsMixedPallet { get; init; }
    public IReadOnlyList<ProductionPalletScanLine> Lines { get; init; } = Array.Empty<ProductionPalletScanLine>();
    public int PalletIndex { get; init; }
    public int PalletCount { get; init; }
    public string PalletStatus { get; init; } = ProductionPalletStatus.Planned;
    public string EffectiveStatus { get; init; } = ProductionPalletStatus.Planned;
    public bool CanFill { get; init; }
    public ProductionPalletDocument? Document { get; init; }

    public static ProductionPalletScanResult Failure(string error)
    {
        return new ProductionPalletScanResult { Success = false, Error = error, ErrorMessage = error };
    }

    public static ProductionPalletScanResult Failure(string code, string message)
    {
        return new ProductionPalletScanResult { Success = false, Error = code, ErrorMessage = message };
    }
}

public sealed class ProductionPalletScanLine
{
    public long ComponentLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public double Qty { get; init; }
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public DateTime? FilledAt { get; init; }
    public bool IsCompleted { get; init; }
    public string Uom { get; init; } = "шт";
}

public sealed class ProductionPalletFillResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorMessage { get; init; }
    public bool AlreadyFilled { get; init; }
    public bool PrdAutoClosed { get; init; }
    public long? ClosedPrdDocId { get; init; }
    public string? ClosedPrdDocRef { get; init; }
    public ProductionPallet? Pallet { get; init; }
    public ProductionPalletDocument? Document { get; init; }
    public string EffectiveStatus { get; init; } = string.Empty;
    public int FilledComponentCount { get; init; }
    public int TotalComponentCount { get; init; }
    public bool LedgerWritten { get; init; }
    public string? Message { get; init; }
    public ProductionOperationProgress? Progress { get; init; }

    public static ProductionPalletFillResult Failure(string error)
    {
        return new ProductionPalletFillResult { Success = false, Error = error, ErrorMessage = error };
    }

    public static ProductionPalletFillResult Failure(string code, string message)
    {
        return new ProductionPalletFillResult { Success = false, Error = code, ErrorMessage = message };
    }
}

public sealed class OrderProducedStockReleaseResult
{
    public long OrderId { get; init; }
    public long OrderLineId { get; init; }
    public int ReleasedPalletCount { get; init; }
    public IReadOnlyList<string> ReleasedHuCodes { get; init; } = Array.Empty<string>();
    public double ReleasedQty { get; init; }
}

public sealed class OrderProducedStockReleaseException : Exception
{
    public OrderProducedStockReleaseException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Stable machine-readable error codes for the server-authoritative production pallet
/// constructor (plan preview / explicit plan confirm) and the legacy /plan guard.
/// WPF decides on the code; the Russian message is display-only.
/// </summary>
public static class ProductionPalletPlanErrorCodes
{
    public const string LegacyMixedGroupNotSupported = "LEGACY_MIXED_GROUP_NOT_SUPPORTED";
    public const string InvalidPalletPlan = "INVALID_PALLET_PLAN";
    public const string PlanPreviewStale = "PLAN_PREVIEW_STALE";
    public const string NoProductionRequired = "NO_PRODUCTION_REQUIRED";
    public const string PalletOverCapacity = "PALLET_OVER_CAPACITY";
    public const string PalletCapacityMismatch = "PALLET_CAPACITY_MISMATCH";
    public const string LineAllocationMismatch = "LINE_ALLOCATION_MISMATCH";
    public const string OrderLineNotFound = "ORDER_LINE_NOT_FOUND";
    public const string OrderLineCancelled = "ORDER_LINE_CANCELLED";
    public const string OrderNotPlannable = "ORDER_NOT_PLANNABLE";
}

/// <summary>
/// Structured failure for pallet plan preview/confirm. Carries a stable
/// <see cref="ErrorCode"/>, an optional <see cref="Details"/> payload
/// (e.g. per-line allocation mismatches) and, for stale/conflict cases, the
/// server's current preview fingerprint so the client can refresh.
/// </summary>
public sealed class ProductionPalletPlanException : InvalidOperationException
{
    public ProductionPalletPlanException(
        string errorCode,
        string message,
        object? details = null,
        string? currentPreviewFingerprint = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
        CurrentPreviewFingerprint = currentPreviewFingerprint;
    }

    public string ErrorCode { get; }
    public object? Details { get; }
    public string? CurrentPreviewFingerprint { get; }
}

/// <summary>Per-line allocation problem for <c>LINE_ALLOCATION_MISMATCH.details.lines[]</c>.</summary>
public sealed record LineAllocationMismatchDetail(
    long OrderLineId,
    double RequiredQty,
    double AllocatedQty,
    double DifferenceQty);

/// <summary>One order line in the plan preview with its current server-derived shortfall.</summary>
public sealed record ProductionPalletPlanPreviewLine(
    long OrderLineId,
    long ItemId,
    string ItemName,
    double? MaxQtyPerHu,
    double ShortfallQty);

/// <summary>Temporary (unsaved) pallet proposed by the server auto-split; also the confirm input shape.</summary>
public sealed record SuggestedPalletComponentDto(
    long OrderLineId,
    long ItemId,
    string ItemName,
    double Qty);

public sealed record SuggestedPalletDto(
    int TempNo,
    double? CapacityQty,
    double TotalQty,
    bool IsMixed,
    IReadOnlyList<SuggestedPalletComponentDto> Components);

/// <summary>A component of an already-saved (open or historical) pallet, read-only in the constructor.</summary>
public sealed record SavedPalletComponentDto(
    long ProductionPalletLineId,
    long? OrderLineId,
    long ItemId,
    string ItemName,
    double PlannedQty,
    double FilledQty,
    bool IsCompleted);

/// <summary>An already-saved physical pallet. Never used as confirm input.</summary>
public sealed record SavedPalletDto(
    string Kind,
    long PalletId,
    string HuCode,
    long PrdDocId,
    string PrdRef,
    string Status,
    string EffectiveStatus,
    double? CapacityQty,
    double TotalQty,
    bool IsMixed,
    bool HasComponentProgress,
    bool CanDelete,
    string? DisabledReason,
    IReadOnlyList<SavedPalletComponentDto> Components)
{
    public const string OpenKind = "open";
    public const string HistoricalKind = "historical";
}

/// <summary>
/// Server-authoritative preview for the pallet constructor. Three explicit sections:
/// editable <see cref="SuggestedPallets"/> (current shortfall delta), read-only
/// <see cref="OpenPlanPallets"/> (PLANNED/PRINTED/PARTIALLY_FILLED) and read-only
/// <see cref="HistoricalPallets"/> (FILLED). Existing pallets never gate a delta append.
/// </summary>
public sealed class ProductionPalletPlanPreview
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public bool ProductionRequired { get; init; }
    public string PreviewFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPalletPlanPreviewLine> Lines { get; init; } = Array.Empty<ProductionPalletPlanPreviewLine>();
    public IReadOnlyList<SuggestedPalletDto> SuggestedPallets { get; init; } = Array.Empty<SuggestedPalletDto>();
    public IReadOnlyList<SavedPalletDto> OpenPlanPallets { get; init; } = Array.Empty<SavedPalletDto>();
    public IReadOnlyList<SavedPalletDto> HistoricalPallets { get; init; } = Array.Empty<SavedPalletDto>();
}

/// <summary>
/// Confirm request: only the fingerprint and the suggested delta pallets (order_line_id + qty).
/// List element types are nullable because malformed JSON (<c>pallets: [null]</c> /
/// <c>components: [null]</c>) yields null elements; the server validates them explicitly and
/// rejects with INVALID_PALLET_PLAN before touching any property.
/// </summary>
public sealed record ProductionPalletExplicitPlanComponent(long OrderLineId, double Qty);

public sealed record ProductionPalletExplicitPlanPallet(IReadOnlyList<ProductionPalletExplicitPlanComponent?> Components);

public sealed record ProductionPalletExplicitPlanRequest(
    string PreviewFingerprint,
    IReadOnlyList<ProductionPalletExplicitPlanPallet?> Pallets);
