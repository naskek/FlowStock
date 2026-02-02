namespace LightWms.Core.Models;

public sealed class Doc
{
    public long Id { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public DocType Type { get; init; }
    public DocStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public long? PartnerId { get; init; }
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? ShippingRef { get; init; }
    public string? Comment { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }

    public string TypeDisplay => DocTypeMapper.ToDisplayName(Type);

    public string StatusDisplay => DocTypeMapper.StatusToDisplayName(Status);

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

    public string HuDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ShippingRef))
            {
                return string.Empty;
            }

            if (!ShippingRef.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return $"HU: {ShippingRef}";
        }
    }
}
