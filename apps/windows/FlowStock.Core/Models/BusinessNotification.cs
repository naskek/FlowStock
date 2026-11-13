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
    public BusinessNotificationPayload? Payload { get; init; }
}

public sealed record BusinessNotificationPayload
{
    public int Version { get; init; } = 1;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderType { get; init; }
    public string? PartnerName { get; init; }
    public long? DocId { get; init; }
    public string? DocRef { get; init; }
    public string? CorrelationId { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<BusinessNotificationQuantity>? ShipmentLines { get; init; }
}

public sealed record BusinessNotificationQuantity(string ItemName, double Ordered, double Shipped, double Remaining);

public sealed record BusinessNotificationQuery(
    bool UnreadOnly = true, int Limit = 50, int Offset = 0,
    string? Search = null, string? Category = null, DateTime? FromUtc = null, DateTime? ToUtc = null);

public sealed record BusinessNotificationPage
{
    public IReadOnlyList<BusinessNotification> Items { get; init; } = Array.Empty<BusinessNotification>();
    public int Total { get; init; }
    public int UnreadCount { get; init; }
}
