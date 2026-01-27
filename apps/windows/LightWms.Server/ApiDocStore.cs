using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LightWms.Server;

public sealed class ApiDocStore
{
    private readonly string _dbPath;

    public ApiDocStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public void AddApiDoc(string docUid, long docId, string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO api_docs(doc_uid, doc_id, status, created_at)
VALUES(@doc_uid, @doc_id, @status, @created_at);
";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@doc_id", docId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@created_at", DateTime.Now.ToString("s"));
        command.ExecuteNonQuery();
    }

    public (long DocId, string Status, string DocRef)? GetApiDoc(string docUid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT d.id, ad.status, d.doc_ref
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

        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
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

    public void RecordEvent(string eventId, string eventType, string? docUid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO api_events(event_id, event_type, doc_uid, created_at)
VALUES(@event_id, @event_type, @doc_uid, @created_at);
";
        command.Parameters.AddWithValue("@event_id", eventId);
        command.Parameters.AddWithValue("@event_type", eventType);
        command.Parameters.AddWithValue("@doc_uid", string.IsNullOrWhiteSpace(docUid) ? DBNull.Value : docUid);
        command.Parameters.AddWithValue("@created_at", DateTime.Now.ToString("s"));
        command.ExecuteNonQuery();
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
