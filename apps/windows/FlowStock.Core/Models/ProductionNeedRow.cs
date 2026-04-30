namespace FlowStock.Core.Models;

public sealed class ProductionNeedRow
{
    public long ItemId { get; init; }
    public string? Gtin { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemTypeName { get; init; }
    public double PhysicalStockQty { get; init; }
    public double ActiveCustomerOrderOpenQty { get; init; }
    public double ReservedCustomerOrderQty { get; init; }
    public double FreeStockQty { get; init; }
    public double MinStockQty { get; init; }
    public double ProductionNeedQty { get; init; }
}
