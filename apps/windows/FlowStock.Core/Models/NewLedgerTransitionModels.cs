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
