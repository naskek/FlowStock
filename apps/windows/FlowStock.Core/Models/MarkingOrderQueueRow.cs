namespace FlowStock.Core.Models;

public sealed class MarkingOrderQueueRow
{
    public Guid? MarkingOrderId { get; init; }
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public string? SourceType { get; init; }
    public long? ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? Gtin { get; init; }
    public OrderStatus OrderStatus { get; init; }
    public DateTime? DueDate { get; init; }
    public MarkingStatus MarkingStatus { get; init; }
    public int MarkingLineCount { get; init; }
    public double MarkingCodeCount { get; init; }
    public DateTime? LastGeneratedAt { get; init; }

    public string PartnerDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PartnerCode) && !string.IsNullOrWhiteSpace(PartnerName))
            {
                return $"{PartnerCode} - {PartnerName}";
            }

            return PartnerName ?? PartnerCode ?? string.Empty;
        }
    }
}
