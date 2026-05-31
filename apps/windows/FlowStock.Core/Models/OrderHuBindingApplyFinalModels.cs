namespace FlowStock.Core.Models;

public sealed class OrderHuBindingApplyFinalRequest
{
    public const string ReplaceFinalSelectionMode = "replace_final_selection";

    public string Mode { get; init; } = string.Empty;
    public IReadOnlyList<OrderHuBindingApplyFinalLineRequest> Lines { get; init; } =
        Array.Empty<OrderHuBindingApplyFinalLineRequest>();
}

public sealed class OrderHuBindingApplyFinalLineRequest
{
    public long OrderLineId { get; init; }
    public IReadOnlyList<string>? ExpectedBoundHuCodes { get; init; }
    public IReadOnlyList<string>? FinalHuCodes { get; init; }
}

public sealed class OrderHuBindingApplyFinalResult
{
    public bool Ok { get; init; } = true;
    public long OrderId { get; init; }
    public IReadOnlyList<OrderHuBindingApplyFinalLineResult> AppliedLines { get; init; } =
        Array.Empty<OrderHuBindingApplyFinalLineResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class OrderHuBindingApplyFinalLineResult
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public IReadOnlyList<string> PreviousHuCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FinalHuCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BoundHuCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DetachedHuCodes { get; init; } = Array.Empty<string>();
    public double ReservedQty { get; init; }
    public int CancelledPlannedPalletCount { get; init; }
    public double RestoredPlannedQty { get; init; }
}
