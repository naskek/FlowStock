namespace FlowStock.Core.Models;

public sealed class ImportedEvent
{
    public string EventId { get; init; } = string.Empty;
    public DateTime ImportedAt { get; init; }
    public string SourceFile { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
}

