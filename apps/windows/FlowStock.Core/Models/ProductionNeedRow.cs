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
    public double TotalToMakeQty { get; init; }
}
