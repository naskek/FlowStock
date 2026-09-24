namespace FlowStock.Core.Models;

public static class OrderCoverageTransferType
{
    public const string PlannedPalletAdoption = "PLANNED_PALLET_ADOPTION";
}

public static class OrderCoverageCompensationKind
{
    public const string ReturnPlan = "RETURN_PLAN";
}

public sealed class OrderCoverageTransfer
{
    public long Id { get; init; }
    public string TransferType { get; init; } = OrderCoverageTransferType.PlannedPalletAdoption;
    public long SourceOrderId { get; init; }
    public string SourceOrderRef { get; init; } = string.Empty;
    public string SourceOrderStatus { get; init; } = string.Empty;
    public long TargetOrderId { get; init; }
    public string TargetOrderRef { get; init; } = string.Empty;
    public long SourcePrdDocId { get; init; }
    public string SourcePrdDocRef { get; init; } = string.Empty;
    public long TargetPrdDocId { get; init; }
    public string TargetPrdDocRef { get; init; } = string.Empty;
    public long ProductionPalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public string PalletStatusAtTransfer { get; init; } = string.Empty;
    public DateTime? PrintedAtAtTransfer { get; init; }
    public double TransferredQty { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CompensationKind { get; init; }
    public DateTime? CompensatedAt { get; init; }
    public IReadOnlyList<OrderCoverageTransferLine> Lines { get; init; } =
        Array.Empty<OrderCoverageTransferLine>();
}

public sealed class OrderCoverageTransferLine
{
    public long Id { get; init; }
    public long TransferId { get; init; }
    public long SourceOrderLineId { get; init; }
    public long TargetOrderLineId { get; init; }
    public long SourceDocLineId { get; init; }
    public long? SourceProductionPalletLineId { get; init; }
    public long ItemId { get; init; }
    public double SourceQtyOrderedBefore { get; init; }
    public double TransferredQty { get; init; }
    public string SourceProductionPurpose { get; init; } = string.Empty;
    public string? SourceProductionPalletGroup { get; init; }
}

public sealed class OrderCoveragePlanCompensation
{
    public long TransferId { get; init; }
    public long SourceOrderId { get; init; }
    public long TargetOrderId { get; init; }
    public long TargetPrdDocId { get; init; }
    public long RestoredSourcePrdDocId { get; init; }
    public long ProductionPalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long? RestoredSourceOrderLineId { get; init; }
    public IReadOnlyList<OrderCoveragePlanCompensationLine> Lines { get; init; } =
        Array.Empty<OrderCoveragePlanCompensationLine>();
}

public sealed class OrderCoveragePlanCompensationLine
{
    public long SourceDocLineId { get; init; }
    public long? SourceProductionPalletLineId { get; init; }
    public long TargetOrderLineId { get; init; }
    public long RestoredSourceOrderLineId { get; init; }
    public long ItemId { get; init; }
    public double TransferredQty { get; init; }
    public ProductionLinePurpose SourceProductionPurpose { get; init; } = ProductionLinePurpose.InternalStock;
}

