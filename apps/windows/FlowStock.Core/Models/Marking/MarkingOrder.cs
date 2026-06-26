namespace FlowStock.Core.Models.Marking;

public sealed class MarkingOrder
{
    public Guid Id { get; init; }
    public long? OrderId { get; init; }
    public long? OrderLineId { get; init; }
    public long? ItemId { get; init; }
    public string? Gtin { get; init; }
    public int RequestedQuantity { get; init; }
    public string RequestNumber { get; init; } = string.Empty;
    public string Status { get; init; } = MarkingOrderStatus.Draft;
    public string RequestStatus { get; init; } = MarkingRequestStatus.NotRequested;
    public string? Notes { get; init; }
    public string? SourceType { get; init; }
    public long? SourceOrderId { get; init; }
    public DateTime? RequestedAt { get; init; }
    public DateTime? CodesBoundAt { get; init; }
    public DateTime? LastExcelRequestedAt { get; init; }
    public string? LastExcelRequestHash { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
