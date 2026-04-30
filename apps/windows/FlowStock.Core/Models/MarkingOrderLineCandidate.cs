namespace FlowStock.Core.Models;

public sealed class MarkingOrderLineCandidate
{
    public long OrderId { get; init; }
    public long OrderLineId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Gtin { get; init; }
    public bool ItemTypeEnableMarking { get; init; }
    public double QtyOrdered { get; init; }
    public double ShippedQty { get; init; }
    public double ReservedQty { get; init; }
    public double QtyForMarking { get; init; }
}
