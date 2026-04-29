namespace FlowStock.Core.Models;

public sealed class HuOrderContextRow
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }

    public long? OriginInternalOrderId { get; init; }
    public string? OriginInternalOrderRef { get; init; }

    public long? ReservedCustomerOrderId { get; init; }
    public string? ReservedCustomerOrderRef { get; init; }
    public long? ReservedCustomerId { get; init; }
    public string? ReservedCustomerName { get; init; }
}
