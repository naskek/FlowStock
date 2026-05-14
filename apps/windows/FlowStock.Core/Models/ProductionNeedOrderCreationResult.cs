namespace FlowStock.Core.Models;

public sealed class ProductionNeedOrderCreationResult
{
    public int CustomerDraftCount { get; init; }
    public int InternalDraftCount { get; init; }
    public int CreatedLineCount { get; init; }
    public double CreatedQty { get; init; }
    public IReadOnlyList<long> CustomerDraftOrderIds { get; init; } = Array.Empty<long>();
    public long? InternalDraftOrderId { get; init; }
    public IReadOnlyList<string> DebugSummary { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = string.Empty;
}

public sealed class ProductionNeedOrderDraftRequestLine
{
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
}
