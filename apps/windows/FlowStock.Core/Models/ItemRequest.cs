namespace FlowStock.Core.Models;

public sealed class ItemRequest
{
    public long Id { get; init; }
    public string Barcode { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public string? DeviceId { get; init; }
    public string? Login { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Status { get; init; } = "NEW";
    public DateTime? ResolvedAt { get; init; }
}
