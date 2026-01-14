using System.Globalization;
using LightWms.Core.Abstractions;
using LightWms.Core.Models;
using Microsoft.Data.Sqlite;

namespace LightWms.Data;

public sealed class SqliteDataStore : IDataStore
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _connection;
    private readonly SqliteTransaction? _transaction;

    public SqliteDataStore(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={dbPath}";
    }

    private SqliteDataStore(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        _connectionString = connection.ConnectionString;
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    barcode TEXT UNIQUE,
    gtin TEXT,
    uom TEXT
);
CREATE TABLE IF NOT EXISTS locations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS docs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_ref TEXT NOT NULL,
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    closed_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_docs_ref_type ON docs(doc_ref, type);
CREATE TABLE IF NOT EXISTS doc_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    qty REAL NOT NULL,
    from_location_id INTEGER,
    to_location_id INTEGER
);
CREATE INDEX IF NOT EXISTS ix_doc_lines_doc ON doc_lines(doc_id);
CREATE TABLE IF NOT EXISTS ledger (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    location_id INTEGER NOT NULL,
    qty_delta REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_ledger_item_location ON ledger(item_id, location_id);
CREATE TABLE IF NOT EXISTS imported_events (
    event_id TEXT PRIMARY KEY,
    imported_at TEXT NOT NULL,
    source_file TEXT NOT NULL,
    device_id TEXT
);
CREATE TABLE IF NOT EXISTS import_errors (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT,
    reason TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    created_at TEXT NOT NULL
);
";
        command.ExecuteNonQuery();
    }

    public void ExecuteInTransaction(Action<IDataStore> work)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var scoped = new SqliteDataStore(connection, transaction);
        work(scoped);

        transaction.Commit();
    }

    public Item? FindItemByBarcode(string barcode)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, barcode, gtin, uom FROM items WHERE barcode = @barcode");
            command.Parameters.AddWithValue("@barcode", barcode);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        });
    }

    public Item? FindItemById(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, barcode, gtin, uom FROM items WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        });
    }

    public IReadOnlyList<Item> GetItems(string? search)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildItemsQuery(search));
            if (!string.IsNullOrWhiteSpace(search))
            {
                command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }

            using var reader = command.ExecuteReader();
            var items = new List<Item>();
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }

            return items;
        });
    }

    public long AddItem(Item item)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO items(name, barcode, gtin, uom)
VALUES(@name, @barcode, @gtin, @uom);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@name", item.Name);
            command.Parameters.AddWithValue("@barcode", (object?)item.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("@gtin", (object?)item.Gtin ?? DBNull.Value);
            command.Parameters.AddWithValue("@uom", (object?)item.Uom ?? DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateItemBarcode(long itemId, string barcode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE items SET barcode = @barcode WHERE id = @id");
            command.Parameters.AddWithValue("@barcode", barcode);
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public Location? FindLocationByCode(string code)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name FROM locations WHERE code = @code");
            command.Parameters.AddWithValue("@code", code);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadLocation(reader) : null;
        });
    }

    public IReadOnlyList<Location> GetLocations()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name FROM locations ORDER BY code");
            using var reader = command.ExecuteReader();
            var locations = new List<Location>();
            while (reader.Read())
            {
                locations.Add(ReadLocation(reader));
            }

            return locations;
        });
    }

    public long AddLocation(Location location)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO locations(code, name)
VALUES(@code, @name);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@code", location.Code);
            command.Parameters.AddWithValue("@name", location.Name);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public Doc? FindDocByRef(string docRef, DocType type)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, doc_ref, type, status, created_at, closed_at FROM docs WHERE doc_ref = @doc_ref AND type = @type");
            command.Parameters.AddWithValue("@doc_ref", docRef);
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(type));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDoc(reader) : null;
        });
    }

    public Doc? GetDoc(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, doc_ref, type, status, created_at, closed_at FROM docs WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDoc(reader) : null;
        });
    }

    public IReadOnlyList<Doc> GetDocs()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, doc_ref, type, status, created_at, closed_at FROM docs ORDER BY created_at DESC");
            using var reader = command.ExecuteReader();
            var docs = new List<Doc>();
            while (reader.Read())
            {
                docs.Add(ReadDoc(reader));
            }

            return docs;
        });
    }

    public long AddDoc(Doc doc)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, closed_at)
VALUES(@doc_ref, @type, @status, @created_at, @closed_at);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(doc.Type));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
            command.Parameters.AddWithValue("@created_at", ToDbDate(doc.CreatedAt));
            command.Parameters.AddWithValue("@closed_at", doc.ClosedAt.HasValue ? ToDbDate(doc.ClosedAt.Value) : DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<DocLine> GetDocLines(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, doc_id, item_id, qty, from_location_id, to_location_id FROM doc_lines WHERE doc_id = @doc_id ORDER BY id");
            command.Parameters.AddWithValue("@doc_id", docId);
            using var reader = command.ExecuteReader();
            var lines = new List<DocLine>();
            while (reader.Read())
            {
                lines.Add(ReadDocLine(reader));
            }

            return lines;
        });
    }

    public IReadOnlyList<DocLineView> GetDocLineViews(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.id, i.name, i.barcode, dl.qty, lf.code, lt.code
FROM doc_lines dl
INNER JOIN items i ON i.id = dl.item_id
LEFT JOIN locations lf ON lf.id = dl.from_location_id
LEFT JOIN locations lt ON lt.id = dl.to_location_id
WHERE dl.doc_id = @doc_id
ORDER BY dl.id;
");
            command.Parameters.AddWithValue("@doc_id", docId);
            using var reader = command.ExecuteReader();
            var lines = new List<DocLineView>();
            while (reader.Read())
            {
                lines.Add(new DocLineView
                {
                    Id = reader.GetInt64(0),
                    ItemName = reader.GetString(1),
                    Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Qty = reader.GetDouble(3),
                    FromLocation = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ToLocation = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }

            return lines;
        });
    }

    public long AddDocLine(DocLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO doc_lines(doc_id, item_id, qty, from_location_id, to_location_id)
VALUES(@doc_id, @item_id, @qty, @from_location_id, @to_location_id);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@doc_id", line.DocId);
            command.Parameters.AddWithValue("@item_id", line.ItemId);
            command.Parameters.AddWithValue("@qty", line.Qty);
            command.Parameters.AddWithValue("@from_location_id", (object?)line.FromLocationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@to_location_id", (object?)line.ToLocationId ?? DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateDocLineQty(long docLineId, double qty)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE doc_lines SET qty = @qty WHERE id = @id");
            command.Parameters.AddWithValue("@qty", qty);
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteDocLine(long docLineId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM doc_lines WHERE id = @id");
            command.Parameters.AddWithValue("@id", docLineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocStatus(long docId, DocStatus status, DateTime? closedAt)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE docs SET status = @status, closed_at = @closed_at WHERE id = @id");
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(status));
            command.Parameters.AddWithValue("@closed_at", closedAt.HasValue ? ToDbDate(closedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void AddLedgerEntry(LedgerEntry entry)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta)
VALUES(@ts, @doc_id, @item_id, @location_id, @qty_delta);
");
            command.Parameters.AddWithValue("@ts", ToDbDate(entry.Timestamp));
            command.Parameters.AddWithValue("@doc_id", entry.DocId);
            command.Parameters.AddWithValue("@item_id", entry.ItemId);
            command.Parameters.AddWithValue("@location_id", entry.LocationId);
            command.Parameters.AddWithValue("@qty_delta", entry.QtyDelta);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<StockRow> GetStock(string? search)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildStockQuery(search));
            if (!string.IsNullOrWhiteSpace(search))
            {
                command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }

            using var reader = command.ExecuteReader();
            var rows = new List<StockRow>();
            while (reader.Read())
            {
                rows.Add(new StockRow
                {
                    ItemName = reader.GetString(0),
                    Barcode = reader.IsDBNull(1) ? null : reader.GetString(1),
                    LocationCode = reader.GetString(2),
                    Qty = reader.GetDouble(3)
                });
            }

            return rows;
        });
    }

    public double GetLedgerBalance(long itemId, long locationId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT COALESCE(SUM(qty_delta), 0) FROM ledger WHERE item_id = @item_id AND location_id = @location_id");
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@location_id", locationId);
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        });
    }

    public bool IsEventImported(string eventId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM imported_events WHERE event_id = @event_id LIMIT 1");
            command.Parameters.AddWithValue("@event_id", eventId);
            return command.ExecuteScalar() != null;
        });
    }

    public void AddImportedEvent(ImportedEvent ev)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO imported_events(event_id, imported_at, source_file, device_id)
VALUES(@event_id, @imported_at, @source_file, @device_id);
");
            command.Parameters.AddWithValue("@event_id", ev.EventId);
            command.Parameters.AddWithValue("@imported_at", ToDbDate(ev.ImportedAt));
            command.Parameters.AddWithValue("@source_file", ev.SourceFile);
            command.Parameters.AddWithValue("@device_id", string.IsNullOrWhiteSpace(ev.DeviceId) ? DBNull.Value : ev.DeviceId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public long AddImportError(ImportError err)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO import_errors(event_id, reason, raw_json, created_at)
VALUES(@event_id, @reason, @raw_json, @created_at);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@event_id", (object?)err.EventId ?? DBNull.Value);
            command.Parameters.AddWithValue("@reason", err.Reason);
            command.Parameters.AddWithValue("@raw_json", err.RawJson);
            command.Parameters.AddWithValue("@created_at", ToDbDate(err.CreatedAt));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<ImportError> GetImportErrors(string? reason)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildImportErrorsQuery(reason));
            if (!string.IsNullOrWhiteSpace(reason))
            {
                command.Parameters.AddWithValue("@reason", reason.Trim());
            }

            using var reader = command.ExecuteReader();
            var errors = new List<ImportError>();
            while (reader.Read())
            {
                errors.Add(ReadImportError(reader));
            }

            return errors;
        });
    }

    public ImportError? GetImportError(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, event_id, reason, raw_json, created_at FROM import_errors WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadImportError(reader) : null;
        });
    }

    public void DeleteImportError(long id)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM import_errors WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    private T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        if (_connection != null)
        {
            return action(_connection);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return action(connection);
    }

    private SqliteCommand CreateCommand(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction != null)
        {
            command.Transaction = _transaction;
        }

        return command;
    }

    private static Item ReadItem(SqliteDataReader reader)
    {
        return new Item
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
            Gtin = reader.IsDBNull(3) ? null : reader.GetString(3),
            Uom = reader.IsDBNull(4) ? null : reader.GetString(4)
        };
    }

    private static Location ReadLocation(SqliteDataReader reader)
    {
        return new Location
        {
            Id = reader.GetInt64(0),
            Code = reader.GetString(1),
            Name = reader.GetString(2)
        };
    }

    private static Doc ReadDoc(SqliteDataReader reader)
    {
        var type = DocTypeMapper.FromOpString(reader.GetString(2)) ?? DocType.Inbound;
        var status = DocTypeMapper.StatusFromString(reader.GetString(3)) ?? DocStatus.Draft;

        return new Doc
        {
            Id = reader.GetInt64(0),
            DocRef = reader.GetString(1),
            Type = type,
            Status = status,
            CreatedAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue,
            ClosedAt = reader.IsDBNull(5) ? null : FromDbDate(reader.GetString(5))
        };
    }

    private static DocLine ReadDocLine(SqliteDataReader reader)
    {
        return new DocLine
        {
            Id = reader.GetInt64(0),
            DocId = reader.GetInt64(1),
            ItemId = reader.GetInt64(2),
            Qty = reader.GetDouble(3),
            FromLocationId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            ToLocationId = reader.IsDBNull(5) ? null : reader.GetInt64(5)
        };
    }

    private static ImportError ReadImportError(SqliteDataReader reader)
    {
        return new ImportError
        {
            Id = reader.GetInt64(0),
            EventId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Reason = reader.GetString(2),
            RawJson = reader.GetString(3),
            CreatedAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue
        };
    }

    private static string BuildItemsQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return "SELECT id, name, barcode, gtin, uom FROM items ORDER BY name";
        }

        return "SELECT id, name, barcode, gtin, uom FROM items WHERE name LIKE @search OR barcode LIKE @search ORDER BY name";
    }

    private static string BuildStockQuery(string? search)
    {
        var baseQuery = @"
SELECT i.name, i.barcode, l.code, SUM(led.qty_delta) AS qty
FROM ledger led
INNER JOIN items i ON i.id = led.item_id
INNER JOIN locations l ON l.id = led.location_id
";

        if (!string.IsNullOrWhiteSpace(search))
        {
            baseQuery += "WHERE i.name LIKE @search OR i.barcode LIKE @search OR l.code LIKE @search\n";
        }

        baseQuery += "GROUP BY i.id, l.id HAVING qty != 0 ORDER BY i.name, l.code";
        return baseQuery;
    }

    private static string BuildImportErrorsQuery(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "SELECT id, event_id, reason, raw_json, created_at FROM import_errors ORDER BY created_at DESC";
        }

        return "SELECT id, event_id, reason, raw_json, created_at FROM import_errors WHERE reason = @reason ORDER BY created_at DESC";
    }

    private static string ToDbDate(DateTime value)
    {
        return value.ToString("s", CultureInfo.InvariantCulture);
    }

    private static DateTime? FromDbDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
