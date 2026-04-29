namespace FlowStock.Core.Models;

public sealed class StockRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public string? Hu { get; init; }
    public double Qty { get; init; }
    public string BaseUom { get; init; } = "шт";
    public long? ItemTypeId { get; init; }
    public string? ItemTypeName { get; init; }
    public bool ItemTypeEnableMinStockControl { get; init; }
    public bool ItemTypeMinStockUsesOrderBinding { get; init; }
    public double? MinStockQty { get; init; }
    public double ReservedCustomerOrderQty { get; init; }
    public double AvailableForMinStockQty { get; init; }
}

