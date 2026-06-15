namespace FlowStock.Core.Models;

public sealed class ProductionFillingCompletion
{
    public long OrderId { get; init; }
    public string OperationFingerprint { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
    public string? CompletedByDeviceId { get; init; }
}

public sealed class BusinessNotification
{
    public long Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Severity { get; init; } = "INFO";
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public long? EntityId { get; init; }
    public string? EntityRef { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Source { get; init; } = "SERVER";
    public string DedupeKey { get; init; } = string.Empty;
    public bool IsRead { get; init; }
}
