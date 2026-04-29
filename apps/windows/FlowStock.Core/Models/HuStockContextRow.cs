namespace FlowStock.Core.Models;

public sealed class HuStockContextRow
{
    public string Hu { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public double Qty { get; init; }

    public long? OriginInternalOrderId { get; init; }
    public string? OriginInternalOrderRef { get; init; }

    public long? ReservedCustomerOrderId { get; init; }
    public string? ReservedCustomerOrderRef { get; init; }
    public long? ReservedCustomerId { get; init; }
    public string? ReservedCustomerName { get; init; }
}
