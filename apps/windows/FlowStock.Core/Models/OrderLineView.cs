namespace FlowStock.Core.Models;

public sealed class OrderLineView
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double QtyOrdered { get; set; }
    public double QtyShipped { get; set; }
    public double QtyRemaining { get; set; }
    public double QtyAvailable { get; set; }
    public double CanShipNow { get; set; }
    public double Shortage { get; set; }
}

