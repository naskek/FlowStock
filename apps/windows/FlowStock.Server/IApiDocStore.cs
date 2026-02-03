namespace FlowStock.Server;

public interface IApiDocStore
{
    void AddApiDoc(
        string docUid,
        long docId,
        string status,
        string docType,
        string docRef,
        long? partnerId,
        long? fromLocationId,
        long? toLocationId,
        string? fromHu,
        string? toHu,
        string? deviceId);

    ApiDocInfo? GetApiDoc(string docUid);
    void UpdateApiDocStatus(string docUid, string status);
    bool IsEventProcessed(string eventId);
    ApiEventInfo? GetEvent(string eventId);
    void RecordEvent(string eventId, string eventType, string? docUid, string? deviceId, string? rawJson);
    void RecordOpEvent(string eventId, string eventType, string? docUid, string? deviceId, string? rawJson);
    void AddReservationLine(string docUid, long itemId, long locationId, double qty);
    double GetReservedQty(long itemId, long locationId, string? excludeDocUid);
    void ClearReservations(string docUid);
}

