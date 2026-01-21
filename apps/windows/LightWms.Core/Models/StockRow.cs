namespace LightWms.Core.Models;

public sealed class StockRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string BaseUom { get; init; } = "шт";
}
