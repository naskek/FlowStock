namespace FlowStock.Core.Models;

public sealed class Order
{
    public long Id { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public OrderType Type { get; init; } = OrderType.Customer;
    public long? PartnerId { get; init; }
    public DateTime? DueDate { get; init; }
    public OrderStatus Status { get; init; }
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ShippedAt { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public bool UseReservedStock { get; init; }
    public MarkingStatus MarkingStatus { get; init; } = MarkingStatus.NotRequired;
    public DateTime? MarkingExcelGeneratedAt { get; init; }
    public DateTime? MarkingPrintedAt { get; init; }

    public string TypeDisplay => OrderStatusMapper.TypeToDisplayName(Type);
    public string StatusDisplay => OrderStatusMapper.StatusToDisplayName(Status, Type);
    public string MarkingStatusDisplay => MarkingStatusMapper.ToDisplayName(MarkingStatus);
    public string MarkingStatusShortDisplay => MarkingStatusMapper.ToShortDisplayName(MarkingStatus);

    public string PartnerDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PartnerCode) && !string.IsNullOrWhiteSpace(PartnerName))
            {
                return $"{PartnerCode} - {PartnerName}";
            }

            if (!string.IsNullOrWhiteSpace(PartnerCode))
            {
                return PartnerCode;
            }

            return PartnerName ?? string.Empty;
        }
    }
}

