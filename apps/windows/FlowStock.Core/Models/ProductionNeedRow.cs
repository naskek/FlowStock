namespace FlowStock.Core.Models;

public sealed class ProductionNeedRow
{
    public long ItemId { get; init; }
    public DateTime NeedDate { get; init; }
    public string? Gtin { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemTypeName { get; init; }
    public double FreeStockQty { get; init; }
    public double MinStockQty { get; init; }
    public double ToCloseOrdersQty { get; init; }
    public double ToMinStockQty { get; init; }
    public double OpenInternalOrderQty { get; init; }
    public string OpenInternalOrderRefs { get; init; } = string.Empty;
    public double PlannedPalletQty { get; init; }
    public double FilledPalletQty { get; init; }
    public int PlannedPalletCount { get; init; }
    public int FilledPalletCount { get; init; }
    public double RemainingPalletQty { get; init; }
    public double QtyToCreate { get; init; }
    public bool CanCreateOrder { get; init; }
    public string Reason { get; init; } = string.Empty;
    public double TotalToMakeQty { get; init; }
}
