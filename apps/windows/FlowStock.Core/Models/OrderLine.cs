namespace FlowStock.Core.Models;

public sealed class OrderLine
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
    public ProductionLinePurpose ProductionPurpose { get; init; } = ProductionLinePurpose.InternalStock;
    public string? ProductionPalletGroup { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancelledByActor { get; init; }
    public string? CancelledByDeviceId { get; init; }
    public string? CancelReason { get; init; }
    public long Revision { get; init; }
}
