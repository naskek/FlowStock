namespace FlowStock.Server;

public sealed record ApiDocInfo(
    long DocId,
    string Status,
    string DocRef,
    string DocType,
    long? PartnerId,
    long? FromLocationId,
    long? ToLocationId,
    string? FromHu,
    string? ToHu,
    string? DeviceId);

public sealed record ApiEventInfo(string EventType, string? DocUid);
