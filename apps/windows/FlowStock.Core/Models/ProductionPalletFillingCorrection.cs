namespace FlowStock.Core.Models;

public static class ProductionPalletFillingCorrectionAction
{
    public const string CorrectFilled = "CORRECT_FILLED";
    public const string ResetPartial = "RESET_PARTIAL";

    public static bool IsKnown(string? value) =>
        string.Equals(value, CorrectFilled, StringComparison.Ordinal)
        || string.Equals(value, ResetPartial, StringComparison.Ordinal);
}

public static class ProductionPalletFillingCorrectionReasonCode
{
    public const string ErroneousHuFill = "ERRONEOUS_HU_FILL";
    public const string ErroneousPartialFill = "ERRONEOUS_PARTIAL_FILL";

    public static string ForAction(string action) =>
        action switch
        {
            ProductionPalletFillingCorrectionAction.CorrectFilled => ErroneousHuFill,
            ProductionPalletFillingCorrectionAction.ResetPartial => ErroneousPartialFill,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Неизвестное действие корректировки.")
        };
}

public static class ProductionPalletFillingCorrectionErrorCodes
{
    public const string InvalidRequestId = "INVALID_REQUEST_ID";
    public const string HuRequired = "HU_REQUIRED";
    public const string InvalidAction = "INVALID_ACTION";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string ReasonTooLong = "REASON_TOO_LONG";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string BlockDisabled = "BLOCK_DISABLED";
    public const string PalletNotFound = "PALLET_NOT_FOUND";
    public const string CorrectionStateChanged = "CORRECTION_STATE_CHANGED";
    public const string SourcePrdNotDedicated = "SOURCE_PRD_NOT_DEDICATED";
    public const string LedgerMismatch = "LEDGER_MISMATCH";
    public const string LaterLedgerMovement = "LATER_LEDGER_MOVEMENT";
    public const string ActiveReservation = "ACTIVE_RESERVATION";
    public const string ActiveOrderControl = "ACTIVE_ORDER_CONTROL";
    public const string ActiveDraftReference = "ACTIVE_DRAFT_REFERENCE";
    public const string CustomerShipped = "CUSTOMER_SHIPPED";
    public const string MarkingRollbackBlocked = "MARKING_ROLLBACK_BLOCKED";
    public const string AmbiguousReplacementPrd = "AMBIGUOUS_REPLACEMENT_PRD";
    public const string CorPostingFailed = "COR_POSTING_FAILED";
    public const string CorLedgerMismatch = "COR_LEDGER_MISMATCH";
}

public sealed class ProductionPalletFillingCorrectionConfirmRequest
{
    public string RequestId { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public string ExpectedAction { get; init; } = string.Empty;
    public string ReasonText { get; init; } = string.Empty;
    public string? ActorName { get; init; }
    public string? DeviceName { get; init; }
    public string? ClientName { get; init; }
    public string? ClientVersion { get; init; }
}

public sealed class ProductionPalletFillingCorrectionBlocker
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class ProductionPalletFillingCorrectionComponent
{
    public long ComponentId { get; init; }
    public long DocLineId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
}

public sealed class ProductionPalletFillingCorrectionLedgerLine
{
    public long SourceLedgerEntryId { get; init; }
    public long SourceDocLineId { get; init; }
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public double SourceQty { get; init; }
    public double CorrectionQty => -SourceQty;
}

public sealed class ProductionPalletFillingCorrectionPreview
{
    public string HuCode { get; init; } = string.Empty;
    public string? Action { get; init; }
    public bool CanConfirm => Action != null && Blockers.Count == 0;
    public long? SourcePalletId { get; init; }
    public long? SourcePrdDocId { get; init; }
    public string? SourcePrdRef { get; init; }
    public int MarkingCodeCount { get; init; }
    public IReadOnlyList<ProductionPalletFillingCorrectionComponent> Components { get; init; } =
        Array.Empty<ProductionPalletFillingCorrectionComponent>();
    public IReadOnlyList<ProductionPalletFillingCorrectionLedgerLine> LedgerInversion { get; init; } =
        Array.Empty<ProductionPalletFillingCorrectionLedgerLine>();
    public IReadOnlyList<ProductionPalletFillingCorrectionBlocker> Blockers { get; init; } =
        Array.Empty<ProductionPalletFillingCorrectionBlocker>();
}

public sealed class ProductionPalletFillingCorrectionResult
{
    public bool Success { get; init; }
    public bool Replay { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public long? AdjustmentId { get; init; }
    public string? Action { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long? SourcePalletId { get; init; }
    public long? SourcePrdDocId { get; init; }
    public long? CorDocId { get; init; }
    public string? CorDocRef { get; init; }
    public long? ReplacementPalletId { get; init; }
    public long? ReplacementPrdDocId { get; init; }
}

public sealed class ProductionPalletFillingCorrectionHistoryEntry
{
    public long AdjustmentId { get; init; }
    public string Action { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long? SourcePalletId { get; init; }
    public long? SourcePrdDocId { get; init; }
    public long? CorDocId { get; init; }
    public long? ReplacementPalletId { get; init; }
    public long? ReplacementPrdDocId { get; init; }
    public string ReasonText { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed class ProductionPalletFillingAdjustment
{
    public long Id { get; init; }
    public Guid RequestId { get; init; }
    public string PayloadHash { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public long? SourcePalletId { get; init; }
    public long? RootPalletId { get; init; }
    public long? ReplacementPalletId { get; init; }
    public string? ResultJson { get; init; }
}

public sealed class ProductionPalletCorrectionMarkingCode
{
    public Guid Id { get; init; }
    public Guid MarkingOrderId { get; init; }
    public Guid ImportId { get; init; }
    public string Origin { get; init; } = string.Empty;
    public long? MarkingOrderLineId { get; init; }
    public string Status { get; init; } = string.Empty;
    public long? ReceiptDocId { get; init; }
    public long? ReceiptLineId { get; init; }
    public DateTime? AppliedAt { get; init; }
    public string? AppliedAtRaw { get; init; }
    public DateTime? ReportedAt { get; init; }
    public DateTime? IntroducedAt { get; init; }
}

public sealed class ProductionPalletReplacementResult
{
    public long PalletId { get; init; }
    public IReadOnlyDictionary<long, (long DocLineId, long ComponentId)> BySourceComponentId { get; init; } =
        new Dictionary<long, (long DocLineId, long ComponentId)>();
}
