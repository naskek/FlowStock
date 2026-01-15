namespace LightWms.Core.Models;

public sealed class OrderLine
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
}
