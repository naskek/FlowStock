namespace FlowStock.Core.Models.Marking;

public sealed class MarkingOrder
{
    public Guid Id { get; init; }
    public long OrderId { get; init; }
    public long? ItemId { get; init; }
    public string? Gtin { get; init; }
    public int RequestedQuantity { get; init; }
    public string RequestNumber { get; init; } = string.Empty;
    public string Status { get; init; } = MarkingOrderStatus.Draft;
    public string? Notes { get; init; }
    public DateTime? RequestedAt { get; init; }
    public DateTime? CodesBoundAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
