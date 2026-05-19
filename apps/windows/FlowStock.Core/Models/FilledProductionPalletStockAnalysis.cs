namespace FlowStock.Core.Models;

public static class ProductionPalletStockBackfillDecisionCodes
{
    public const string SafeToBackfill = "SAFE_TO_BACKFILL";
    public const string AlreadyShippedSkip = "ALREADY_SHIPPED_SKIP";
    public const string AmbiguousRequiresManualReview = "AMBIGUOUS_REQUIRES_MANUAL_REVIEW";
}

public sealed class FilledProductionPalletStockMetrics
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderStatus { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long? ToLocationId { get; init; }
    public string? ToLocationCode { get; init; }
    public double PlannedQty { get; init; }
    public double CurrentLedgerQty { get; init; }
    public double OutboundBySameHuQty { get; init; }
    public string OutboundDocsBySameHu { get; init; } = string.Empty;
    public double OutboundByOrderItemQty { get; init; }
    public string OutboundDocsByOrderItem { get; init; } = string.Empty;
    public string Status { get; init; } = ProductionPalletStatus.Filled;
    public DateTime? FilledAt { get; init; }
}

public sealed class FilledProductionPalletStockAnalysis
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderStatus { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long? ToLocationId { get; init; }
    public string? ToLocationCode { get; init; }
    public double PlannedQty { get; init; }
    public double CurrentLedgerQty { get; init; }
    public double OutboundBySameHuQty { get; init; }
    public string OutboundDocsBySameHu { get; init; } = string.Empty;
    public double OutboundByOrderItemQty { get; init; }
    public string OutboundDocsByOrderItem { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public double? ExpectedCurrentQty { get; init; }
    public double MissingQty { get; init; }
    public string? Reason { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Filled;
    public DateTime? FilledAt { get; init; }
}

public sealed class ProtectedFilledProductionPallet
{
    public string HuCode { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
}

public sealed class HuBalanceCorrectionCandidateBalance
{
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public bool Protected { get; init; }
}

public sealed class FilledStockReverseCandidate
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderStatus { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long? LocationId { get; init; }
    public string? LocationCode { get; init; }
    public double PlannedQty { get; init; }
    public double CurrentHuStock { get; init; }
    public double OutboundBySameHuQty { get; init; }
    public string OutboundDocsBySameHu { get; init; } = string.Empty;
    public double OutboundByOrderItemQty { get; init; }
    public string OutboundDocsByOrderItem { get; init; } = string.Empty;
    public double ReverseQty { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class ReverseFilledStockBackfillDraftResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public long? DocId { get; init; }
    public string? DocRef { get; init; }
    public int LineCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static ReverseFilledStockBackfillDraftResult Ok(long docId, string docRef, int lineCount, string message, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            DocId = docId,
            DocRef = docRef,
            LineCount = lineCount,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>()
        };

    public static ReverseFilledStockBackfillDraftResult Fail(string error, string message, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = false,
            Error = error,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>()
        };
}

// Legacy alias for backward compatibility in tests mapping ledger_qty.
public sealed class FilledProductionPalletStockGap
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long? ToLocationId { get; init; }
    public double PlannedQty { get; init; }
    public double LedgerQty { get; init; }
    public double MissingQty { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Filled;
    public DateTime? FilledAt { get; init; }

    public static FilledProductionPalletStockGap FromAnalysis(FilledProductionPalletStockAnalysis analysis) =>
        new()
        {
            PalletId = analysis.PalletId,
            PrdDocId = analysis.PrdDocId,
            PrdDocRef = analysis.PrdDocRef,
            ItemId = analysis.ItemId,
            ItemName = analysis.ItemName,
            HuCode = analysis.HuCode,
            ToLocationId = analysis.ToLocationId,
            PlannedQty = analysis.PlannedQty,
            LedgerQty = analysis.CurrentLedgerQty,
            MissingQty = analysis.MissingQty,
            Status = analysis.Status,
            FilledAt = analysis.FilledAt
        };
}
