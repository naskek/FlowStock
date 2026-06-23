namespace FlowStock.Core.Models;

public sealed class CustomerReadinessOrderStatusCandidate
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public OrderStatus OldStatus { get; init; }
    public OrderStatus NewStatus { get; init; }
    public double TotalOrderedQty { get; init; }
    public double TotalShippedQty { get; init; }
    public double TotalCoveredQty { get; init; }
    public double TotalMissingQty { get; init; }
}
