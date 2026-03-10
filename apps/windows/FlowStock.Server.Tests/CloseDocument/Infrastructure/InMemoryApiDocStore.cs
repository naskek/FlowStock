using FlowStock.Server;

namespace FlowStock.Server.Tests.CloseDocument.Infrastructure;

internal sealed class InMemoryApiDocStore : IApiDocStore
{
    private readonly Dictionary<string, ApiDocInfo> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecordedApiEvent> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReservationLine> _reservations = new();

    public ApiDocInfo? GetApiDoc(string docUid)
    {
        return _docs.TryGetValue(docUid, out var info) ? info : null;
    }

    public ApiEventInfo? GetEvent(string eventId)
    {
        return _events.TryGetValue(eventId, out var info)
            ? new ApiEventInfo(info.EventType, info.DocUid)
            : null;
    }

    public bool IsEventProcessed(string eventId)
    {
        return _events.ContainsKey(eventId);
    }

    public int CountEvents(string eventType, string? docUid = null)
    {
        return _events.Values.Count(entry =>
            string.Equals(entry.EventType, eventType, StringComparison.OrdinalIgnoreCase)
            && (docUid == null || string.Equals(entry.DocUid, docUid, StringComparison.OrdinalIgnoreCase)));
    }

    public void AddApiDoc(
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
        string? deviceId)
    {
        _docs[docUid] = new ApiDocInfo(
            docId,
            status,
            docRef,
            docType,
            partnerId,
            fromLocationId,
            toLocationId,
            NormalizeHu(fromHu),
            NormalizeHu(toHu),
            deviceId);
    }

    public void UpdateApiDocHeader(
        string docUid,
        long? partnerId,
        long? fromLocationId,
        long? toLocationId,
        string? fromHu,
        string? toHu)
    {
        if (!_docs.TryGetValue(docUid, out var info))
        {
            return;
        }

        _docs[docUid] = info with
        {
            PartnerId = partnerId,
            FromLocationId = fromLocationId,
            ToLocationId = toLocationId,
            FromHu = NormalizeHu(fromHu),
            ToHu = NormalizeHu(toHu)
        };
    }

    public void UpdateApiDocStatus(string docUid, string status)
    {
        if (!_docs.TryGetValue(docUid, out var info))
        {
            return;
        }

        _docs[docUid] = info with { Status = status };
    }

    public void RecordEvent(string eventId, string eventType, string? docUid, string? deviceId, string? rawJson)
    {
        if (_events.ContainsKey(eventId))
        {
            return;
        }

        _events[eventId] = new RecordedApiEvent(eventId, eventType, docUid, deviceId, rawJson);
    }

    public void RecordOpEvent(string eventId, string eventType, string? docUid, string? deviceId, string? rawJson)
    {
        RecordEvent(eventId, eventType, docUid, deviceId, rawJson);
    }

    public void AddReservationLine(string docUid, long itemId, long locationId, double qty)
    {
        _reservations.Add(new ReservationLine(docUid, itemId, locationId, qty));
    }

    public double GetReservedQty(long itemId, long locationId, string? excludeDocUid)
    {
        return _reservations
            .Where(line => line.ItemId == itemId
                           && line.LocationId == locationId
                           && !string.Equals(line.DocUid, excludeDocUid, StringComparison.OrdinalIgnoreCase))
            .Sum(line => line.Qty);
    }

    public void ClearReservations(string docUid)
    {
        _reservations.RemoveAll(line => string.Equals(line.DocUid, docUid, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record RecordedApiEvent(
        string EventId,
        string EventType,
        string? DocUid,
        string? DeviceId,
        string? RawJson);

    private sealed record ReservationLine(string DocUid, long ItemId, long LocationId, double Qty);
}
