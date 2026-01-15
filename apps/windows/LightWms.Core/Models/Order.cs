namespace LightWms.Core.Models;

public sealed class Order
{
    public long Id { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long PartnerId { get; init; }
    public DateTime? DueDate { get; init; }
    public OrderStatus Status { get; init; }
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ShippedAt { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }

    public string StatusDisplay => OrderStatusMapper.StatusToDisplayName(Status);

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
