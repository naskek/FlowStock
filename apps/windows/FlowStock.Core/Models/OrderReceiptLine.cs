namespace FlowStock.Core.Models;

public sealed class OrderReceiptLine
{
    public long OrderLineId { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double QtyOrdered { get; init; }
    public double QtyReceived { get; init; }
    public double QtyRemaining { get; init; }
    public ProductionLinePurpose ProductionPurpose { get; init; } = ProductionLinePurpose.InternalStock;
    public long? ToLocationId { get; init; }
    public string? ToLocation { get; init; }
    public string? ToHu { get; init; }
    public int SortOrder { get; init; }
}
