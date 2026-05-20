namespace FlowStock.Core.Models;

public sealed class FullyShippedCustomerOrderStatusCandidate
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public OrderStatus OldStatus { get; init; }
    public double TotalOrderedQty { get; init; }
    public double TotalShippedQty { get; init; }
}
