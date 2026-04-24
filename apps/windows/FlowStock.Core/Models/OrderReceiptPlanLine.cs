namespace FlowStock.Core.Models;

public sealed class OrderReceiptPlanLine
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double QtyPlanned { get; init; }
    public long? ToLocationId { get; init; }
    public string? ToLocationCode { get; init; }
    public string? ToLocationName { get; init; }
    public string? ToHu { get; init; }
    public int SortOrder { get; init; }
}
