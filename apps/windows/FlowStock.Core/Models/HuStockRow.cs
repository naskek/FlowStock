namespace FlowStock.Core.Models;

public sealed class HuStockRow
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public double Qty { get; init; }
}

