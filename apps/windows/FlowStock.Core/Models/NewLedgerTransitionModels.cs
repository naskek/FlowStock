namespace FlowStock.Core.Models;

public static class NewLedgerTransitionActionCodes
{
    public const string RemoveStaleReservation = "REMOVE_STALE_RESERVATION";
    public const string RebuildActiveCustomerReservation = "REBUILD_ACTIVE_CUSTOMER_RESERVATION";
    public const string ReportFilledWithoutLedger = "REPORT_FILLED_WITHOUT_LEDGER";
    public const string ReportDraftPrdWithLedger = "REPORT_DRAFT_PRD_WITH_LEDGER";
}

public sealed class NewLedgerTransitionReport
{
    public bool Applied { get; init; }
    public long LedgerRowsBefore { get; init; }
    public long LedgerRowsAfter { get; init; }
    public int StaleReservationCount { get; init; }
    public double StaleReservationQty { get; init; }
    public IReadOnlyList<NewLedgerStaleReservation> StaleReservations { get; init; } = Array.Empty<NewLedgerStaleReservation>();
    public IReadOnlyList<NewLedgerFilledPalletDiagnostic> FilledPalletsWithoutLedger { get; init; } = Array.Empty<NewLedgerFilledPalletDiagnostic>();
    public IReadOnlyList<NewLedgerDraftPrdLedgerDiagnostic> DraftPrdsWithLedger { get; init; } = Array.Empty<NewLedgerDraftPrdLedgerDiagnostic>();
    public IReadOnlyList<NewLedgerTransitionAction> PlannedActions { get; init; } = Array.Empty<NewLedgerTransitionAction>();
}

public sealed class NewLedgerStaleReservation
{
    public long PlanLineId { get; init; }
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ToHu { get; init; } = string.Empty;
    public double Qty { get; init; }
    public double CurrentBalance { get; init; }
}

public sealed class NewLedgerFilledPalletDiagnostic
{
    public long ProductionPalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public double CurrentBalance { get; init; }
}

public sealed class NewLedgerDraftPrdLedgerDiagnostic
{
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public int LedgerRowCount { get; init; }
}

public sealed class NewLedgerTransitionAction
{
    public string ActionCode { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public long? OrderLineId { get; init; }
    public long? ItemId { get; init; }
    public string? HuCode { get; init; }
    public string Details { get; init; } = string.Empty;
}

public static class FilledLedgerRepairDecisions
{
    public const string SafeToBackfill = "SAFE_TO_BACKFILL";
    public const string SkipNotFilled = "SKIP_NOT_FILLED";
    public const string SkipAlreadyHasReceiptLedger = "SKIP_ALREADY_HAS_RECEIPT_LEDGER";
    public const string SkipCancelled = "SKIP_CANCELLED";
    public const string SkipNoHu = "SKIP_NO_HU";
    public const string SkipNoLocation = "SKIP_NO_LOCATION";
    public const string SkipFilteredOut = "SKIP_FILTERED_OUT";
}

public sealed class FilledLedgerRepairRequest
{
    public IReadOnlyList<long> OrderIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> PrdDocIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> PalletIds { get; init; } = Array.Empty<long>();
    public bool CloseStaleInternalPrdDrafts { get; init; }
}

public sealed class FilledLedgerRepairReport
{
    public bool DryRun { get; init; }
    public int LedgerRowsWritten { get; init; }
    public IReadOnlyList<long> AppliedPalletIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> ClosedPrdDocIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> RefreshedOrderIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<FilledLedgerRepairCandidate> Candidates { get; init; } = Array.Empty<FilledLedgerRepairCandidate>();
    public IReadOnlyList<FilledLedgerRepairPrdCloseCandidate> StaleInternalPrdDraftCloseCandidates { get; init; } = Array.Empty<FilledLedgerRepairPrdCloseCandidate>();
    public IReadOnlyList<FilledLedgerRepairCandidate> Skipped { get; init; } = Array.Empty<FilledLedgerRepairCandidate>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class FilledLedgerRepairCandidate
{
    public long? OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public string PrdStatus { get; init; } = string.Empty;
    public long PalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public long? LocationId { get; init; }
    public double PlannedQty { get; init; }
    public double CurrentReceiptQty { get; init; }
    public double CurrentBalanceQty { get; init; }
    public string Decision { get; init; } = string.Empty;
}

public sealed class FilledLedgerRepairPrdCloseCandidate
{
    public long DocId { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public double GrossReceiptQtyByOrder { get; init; }
    public double OrderedQtyByOrder { get; init; }
    public string Reason { get; init; } = string.Empty;
}
