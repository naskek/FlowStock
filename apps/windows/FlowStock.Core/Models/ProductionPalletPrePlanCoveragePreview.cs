namespace FlowStock.Core.Models;

public enum ProductionPalletPlanMode
{
    Full,
    SkipInternalSupply,
    AdoptInternalThenPlan,
    ApplySelectedCoverageThenPlan
}

public sealed class ProductionPalletPrePlanCoveragePreview
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public bool HasWarning { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPalletInternalSupplyWarningLine> Lines { get; init; } =
        Array.Empty<ProductionPalletInternalSupplyWarningLine>();
    public int WouldPlanLineCount { get; init; }
    public int SafeLineCount { get; init; }
    public int WarningLineCount { get; init; }
    public bool HasFreeWarehouseHu { get; init; }
    public IReadOnlyList<ProductionPalletPrePlanFreeHuLine> FreeWarehouseHuLines { get; init; } =
        Array.Empty<ProductionPalletPrePlanFreeHuLine>();
    public IReadOnlyList<ProductionPalletWarehouseHuCandidate> WarehouseHuCandidates { get; init; } =
        Array.Empty<ProductionPalletWarehouseHuCandidate>();
    public IReadOnlyList<ProductionPalletInternalPlannedHuCandidate> InternalPlannedHuCandidates { get; init; } =
        Array.Empty<ProductionPalletInternalPlannedHuCandidate>();
    public IReadOnlyList<ProductionPalletProjectedAdoptionHu> AdoptableInternalPlannedHus { get; init; } =
        Array.Empty<ProductionPalletProjectedAdoptionHu>();
    public IReadOnlyList<ProductionPalletAdoptionSkippedCandidate> AdoptionSkippedCandidates { get; init; } =
        Array.Empty<ProductionPalletAdoptionSkippedCandidate>();
    public int ProjectedAdoptedPalletCount { get; init; }
    public double ProjectedAdoptedQty { get; init; }
    public double ProjectedRemainingQtyAfterAdoption { get; init; }
}

public sealed class ProductionPalletSelectedCoveragePlanRequest
{
    public IReadOnlyList<ProductionPalletSelectedWarehouseHu> SelectedWarehouseHus { get; init; } =
        Array.Empty<ProductionPalletSelectedWarehouseHu>();
    public IReadOnlyList<long> SelectedInternalProductionPalletIds { get; init; } = Array.Empty<long>();
    public bool PlanRemainder { get; init; } = true;
}

public sealed class ProductionPalletSelectedWarehouseHu
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public long TargetOrderLineId { get; init; }
}

public sealed class ProductionPalletSelectedCoverageException : InvalidOperationException
{
    public ProductionPalletSelectedCoverageException(
        string code,
        string message,
        IReadOnlyList<string>? problems = null)
        : base(message)
    {
        Code = code;
        Problems = problems ?? Array.Empty<string>();
    }

    public string Code { get; }
    public IReadOnlyList<string> Problems { get; }
}

public sealed class ProductionPalletWarehouseHuCandidate
{
    public string SourceType { get; init; } = "warehouse";
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public long TargetOrderLineId { get; init; }
    public double Qty { get; init; }
    public string Status { get; init; } = "LEDGER_STOCK";
    public string SourceRef { get; init; } = string.Empty;
    public bool Recommended { get; init; }
    public bool SelectedByDefault { get; init; }
    public string DisabledReason { get; init; } = string.Empty;
}

public sealed class ProductionPalletInternalPlannedHuCandidate
{
    public string SourceType { get; init; } = "internal_plan";
    public long ProductionPalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public long SourcePrdDocId { get; init; }
    public string SourcePrdDocRef { get; init; } = string.Empty;
    public string SourceStatus { get; init; } = string.Empty;
    public long? TargetOrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public string? ProductionPalletGroup { get; init; }
    public bool IsMixed { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Planned;
    public bool Recommended { get; init; }
    public bool SelectedByDefault { get; init; }
    public string DisabledReason { get; init; } = string.Empty;
}

public sealed class ProductionPalletProjectedAdoptionHu
{
    public long ProductionPalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public long SourcePrdDocId { get; init; }
    public string SourcePrdDocRef { get; init; } = string.Empty;
    public string SourceStatus { get; init; } = string.Empty;
    public long? TargetOrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public string? ProductionPalletGroup { get; init; }
    public bool IsMixed { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Planned;
    public bool WillRequireReprint { get; init; }
    public IReadOnlyList<ProductionPalletProjectedAdoptionLine> Lines { get; init; } =
        Array.Empty<ProductionPalletProjectedAdoptionLine>();
}

public sealed class ProductionPalletProjectedAdoptionLine
{
    public long SourceOrderLineId { get; init; }
    public long TargetOrderLineId { get; init; }
    public long DocLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
}

public sealed class ProductionPalletAdoptionSkippedCandidate
{
    public long ProductionPalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public long SourcePrdDocId { get; init; }
    public string SourcePrdDocRef { get; init; } = string.Empty;
    public string SourceStatus { get; init; } = string.Empty;
    public long? TargetOrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public string? ProductionPalletGroup { get; init; }
    public bool IsMixed { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Planned;
    public string SkipReason { get; init; } = string.Empty;
}

public sealed class ProductionPalletPrePlanFreeHuLine
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double WouldPlanQty { get; init; }
    public int FreeHuCount { get; init; }
    public double FreeHuQty { get; init; }
}

public sealed class ProductionPalletInternalSupplyWarningLine
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double WouldPlanQty { get; init; }
    public long InternalOrderId { get; init; }
    public string InternalOrderRef { get; init; } = string.Empty;
    public string InternalOrderStatus { get; init; } = string.Empty;
    public double ExpectedQty { get; init; }
}

public static class ProductionPalletPlanSkippedReason
{
    public const string ExpectedInternalSupply = "expected_internal_supply";
    public const string MixedGroupContainsExpectedInternalSupply = "mixed_group_contains_expected_internal_supply";
    public const string SourcePrdClosed = "source_prd_closed";
    public const string SourcePrdHasLedger = "source_prd_has_ledger";
    public const string StatusNotEligible = "status_not_eligible";
    public const string PartialProgress = "partial_progress";
    public const string QtyExceedsShortage = "qty_exceeds_shortage";
    public const string MixedGroupMismatch = "mixed_group_mismatch";
    public const string MissingSourceOrderLine = "missing_source_order_line";
    public const string SourceQtyWouldViolateCoverage = "source_qty_would_violate_coverage";
}

public sealed class ProductionPalletPlanSkippedLine
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ProductionPalletGroup { get; init; }
    public string SkippedReason { get; init; } = ProductionPalletPlanSkippedReason.ExpectedInternalSupply;
    public long? TriggeredByOrderLineId { get; init; }
    public IReadOnlyList<ProductionPalletInternalSupplyWarningLine> InternalRefs { get; init; } =
        Array.Empty<ProductionPalletInternalSupplyWarningLine>();
}
