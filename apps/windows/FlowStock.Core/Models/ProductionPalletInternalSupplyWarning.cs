namespace FlowStock.Core.Models;

public sealed class ProductionPalletInternalSupplyWarning
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public bool HasWarning { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPalletInternalSupplyWarningLine> Lines { get; init; } =
        Array.Empty<ProductionPalletInternalSupplyWarningLine>();
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
