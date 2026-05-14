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

public sealed class ProductionNeedOrderPreviewResult
{
    public IReadOnlyList<ProductionNeedOrderPreviewLine> Rows { get; init; } = Array.Empty<ProductionNeedOrderPreviewLine>();
    public string Message { get; init; } = string.Empty;
}

public sealed class ProductionNeedOrderPreviewLine
{
    public long ItemId { get; init; }
    public string Gtin { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Reason { get; init; } = string.Empty;
    public double MinStockQty { get; init; }
    public double FreeStockQty { get; init; }
    public double OpenInternalOrderQty { get; init; }
    public double PlannedPalletQty { get; init; }
    public double FilledPalletQty { get; init; }
}

public sealed class ProductionNeedOrderDraftRequestLine
{
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
}
