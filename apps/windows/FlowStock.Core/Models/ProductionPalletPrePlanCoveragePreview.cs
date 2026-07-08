namespace FlowStock.Core.Models;

public enum ProductionPalletPlanMode
{
    Full,
    SkipInternalSupply
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
