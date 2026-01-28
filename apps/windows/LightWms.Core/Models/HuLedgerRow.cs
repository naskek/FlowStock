namespace LightWms.Core.Models;

public sealed class HuLedgerRow
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string BaseUom { get; init; } = "шт";
}
