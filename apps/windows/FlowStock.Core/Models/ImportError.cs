namespace FlowStock.Core.Models;

public sealed class ImportError
{
    public long Id { get; init; }
    public string? EventId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string RawJson { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

