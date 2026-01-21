namespace LightWms.Core.Models;

public sealed class DocLineView
{
    public long Id { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public double Qty { get; init; }
    public double? QtyInput { get; init; }
    public string? UomCode { get; init; }
    public string BaseUom { get; init; } = "шт";
    public string? FromLocation { get; init; }
    public string? ToLocation { get; init; }
}
