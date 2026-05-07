namespace FlowStock.Core.Models;

public sealed class ProductionNeedOrderCreationResult
{
    public int CustomerDraftCount { get; init; }
    public int InternalDraftCount { get; init; }
    public int CreatedLineCount { get; init; }
    public IReadOnlyList<long> CustomerDraftOrderIds { get; init; } = Array.Empty<long>();
    public long? InternalDraftOrderId { get; init; }
    public string Message { get; init; } = string.Empty;
}
