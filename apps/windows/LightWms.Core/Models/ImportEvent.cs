namespace LightWms.Core.Models;

public sealed class ImportEvent
{
    public string EventId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public DocType Type { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public string Barcode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string? FromLocation { get; init; }
    public string? ToLocation { get; init; }
    public long? PartnerId { get; init; }
    public string? PartnerCode { get; init; }
    public string? OrderRef { get; init; }
    public string? ReasonCode { get; init; }
}
