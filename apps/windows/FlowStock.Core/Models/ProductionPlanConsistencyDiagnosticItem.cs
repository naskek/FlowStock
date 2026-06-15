namespace FlowStock.Core.Models;

public static class ProductionPlanConsistencyProblemCode
{
    public const string OrderZeroButPalletsExist = "ORDER_ZERO_BUT_PALLETS_EXIST";
    public const string PalletsExceedOrderQty = "PALLETS_EXCEED_ORDER_QTY";
    public const string PrdLinesExceedOrderQty = "PRD_LINES_EXCEED_ORDER_QTY";
    public const string FilledPalletsWithDraftPrd = "FILLED_PALLETS_WITH_DRAFT_PRD";
    public const string FilledPalletMissingLedger = "FILLED_PALLET_MISSING_LEDGER";
    public const string PartialPalletHasLedger = "PARTIAL_PALLET_HAS_LEDGER";
    public const string PartialPalletInvalidStatus = "PARTIAL_PALLET_INVALID_STATUS";
    public const string ShippedCustomerWithOpenPrd = "SHIPPED_CUSTOMER_WITH_OPEN_PRD";
    public const string MergedOrderWithPalletPlan = "MERGED_ORDER_WITH_PALLET_PLAN";
    public const string ClosedPrdLedgerMismatch = "CLOSED_PRD_LEDGER_MISMATCH";
}

public static class ProductionPlanConsistencySeverity
{
    public const string Error = "ERROR";
    public const string Warning = "WARNING";
}

public sealed class ProductionPlanConsistencyDiagnosticItem
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double OrderQty { get; init; }
    public double OpenPrdDocQty { get; init; }
    public double ClosedPrdDocQty { get; init; }
    public double PrdDocQty { get; init; }
    public double OpenPalletPlannedQty { get; init; }
    public double PalletPlannedQty { get; init; }
    public double PalletFilledQty { get; init; }
    public double LedgerClosedPrdQty { get; init; }
    public double LedgerOpenPrdQty { get; init; }
    public double LedgerPrdQty { get; init; }
    public string Severity { get; init; } = ProductionPlanConsistencySeverity.Error;
    public string ProblemCode { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPlanConsistencyPalletRow> Pallets { get; init; } = Array.Empty<ProductionPlanConsistencyPalletRow>();
    public IReadOnlyList<ProductionPlanConsistencyPrdDocRow> PrdDocs { get; init; } = Array.Empty<ProductionPlanConsistencyPrdDocRow>();
}

public sealed class ProductionPlanConsistencyPalletRow
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string? PrdDocRef { get; init; }
    public long? DocLineId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
}

public sealed class ProductionPlanConsistencyPrdDocRow
{
    public long DocId { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? ClosedAt { get; init; }
    public long? DocLineId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double Qty { get; init; }
}
