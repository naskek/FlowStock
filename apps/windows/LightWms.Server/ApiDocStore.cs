using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LightWms.Server;

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

public sealed class ApiDocStore
{
    private readonly string _dbPath;

    public ApiDocStore(string dbPath)
    {
        _dbPath = dbPath;
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
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO api_docs(
    doc_uid,
    doc_id,
    status,
    created_at,
    doc_type,
    doc_ref,
    partner_id,
    from_location_id,
    to_location_id,
    from_hu,
    to_hu,
    device_id
)
VALUES(
    @doc_uid,
    @doc_id,
    @status,
    @created_at,
    @doc_type,
    @doc_ref,
    @partner_id,
    @from_location_id,
    @to_location_id,
    @from_hu,
    @to_hu,
    @device_id
);
";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@doc_id", docId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@created_at", DateTime.Now.ToString("s"));
        command.Parameters.AddWithValue("@doc_type", docType);
        command.Parameters.AddWithValue("@doc_ref", docRef);
        command.Parameters.AddWithValue("@partner_id", partnerId.HasValue ? partnerId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@from_location_id", fromLocationId.HasValue ? fromLocationId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@to_location_id", toLocationId.HasValue ? toLocationId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@from_hu", string.IsNullOrWhiteSpace(fromHu) ? DBNull.Value : fromHu.Trim());
        command.Parameters.AddWithValue("@to_hu", string.IsNullOrWhiteSpace(toHu) ? DBNull.Value : toHu.Trim());
        command.Parameters.AddWithValue("@device_id", string.IsNullOrWhiteSpace(deviceId) ? DBNull.Value : deviceId.Trim());
        command.ExecuteNonQuery();
    }

    public ApiDocInfo? GetApiDoc(string docUid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    d.id,
    ad.status,
    COALESCE(ad.doc_ref, d.doc_ref),
    COALESCE(ad.doc_type, d.type),
    COALESCE(ad.partner_id, d.partner_id),
    ad.from_location_id,
    ad.to_location_id,
    ad.from_hu,
    ad.to_hu,
    ad.device_id
FROM api_docs ad
INNER JOIN docs d ON d.id = ad.doc_id
WHERE ad.doc_uid = @doc_uid
LIMIT 1;
";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ApiDocInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public void UpdateApiDocStatus(string docUid, string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_docs SET status = @status WHERE doc_uid = @doc_uid;";
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.ExecuteNonQuery();
    }

    public bool IsEventProcessed(string eventId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM api_events WHERE event_id = @event_id LIMIT 1;";
        command.Parameters.AddWithValue("@event_id", eventId);
        return command.ExecuteScalar() != null;
    }

    public ApiEventInfo? GetEvent(string eventId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT event_type, doc_uid
FROM api_events
WHERE event_id = @event_id
LIMIT 1;";
        command.Parameters.AddWithValue("@event_id", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ApiEventInfo(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public void RecordEvent(string eventId, string eventType, string? docUid, string? deviceId, string? rawJson)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT OR IGNORE INTO api_events(event_id, event_type, doc_uid, created_at, received_at, device_id, raw_json)
VALUES(@event_id, @event_type, @doc_uid, @created_at, @received_at, @device_id, @raw_json);
";
        var now = DateTime.Now.ToString("s");
        command.Parameters.AddWithValue("@event_id", eventId);
        command.Parameters.AddWithValue("@event_type", eventType);
        command.Parameters.AddWithValue("@doc_uid", string.IsNullOrWhiteSpace(docUid) ? DBNull.Value : docUid);
        command.Parameters.AddWithValue("@created_at", now);
        command.Parameters.AddWithValue("@received_at", now);
        command.Parameters.AddWithValue("@device_id", string.IsNullOrWhiteSpace(deviceId) ? DBNull.Value : deviceId);
        command.Parameters.AddWithValue("@raw_json", string.IsNullOrWhiteSpace(rawJson) ? DBNull.Value : rawJson);
        command.ExecuteNonQuery();
    }

    public void RecordOpEvent(string eventId, string eventType, string? docUid, string? deviceId, string? rawJson)
    {
        RecordEvent(eventId, eventType, docUid, deviceId, rawJson);
    }

    public void AddReservationLine(string docUid, long itemId, long locationId, double qty)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO stock_reservation_lines(doc_uid, item_id, location_id, qty, created_at)
VALUES(@doc_uid, @item_id, @location_id, @qty, @created_at);
";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@location_id", locationId);
        command.Parameters.AddWithValue("@qty", qty);
        command.Parameters.AddWithValue("@created_at", DateTime.Now.ToString("s"));
        command.ExecuteNonQuery();
    }

    public double GetReservedQty(long itemId, long locationId, string? excludeDocUid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var sql = @"
SELECT COALESCE(SUM(qty), 0)
FROM stock_reservation_lines
WHERE item_id = @item_id AND location_id = @location_id";
        if (!string.IsNullOrWhiteSpace(excludeDocUid))
        {
            sql += " AND doc_uid != @doc_uid";
        }
        command.CommandText = sql;
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@location_id", locationId);
        if (!string.IsNullOrWhiteSpace(excludeDocUid))
        {
            command.Parameters.AddWithValue("@doc_uid", excludeDocUid);
        }

        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result, CultureInfo.InvariantCulture);
    }

    public void ClearReservations(string docUid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM stock_reservation_lines WHERE doc_uid = @doc_uid;";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
