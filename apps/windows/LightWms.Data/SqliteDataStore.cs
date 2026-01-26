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
    private const string DocSelectBase = "SELECT d.id, d.doc_ref, d.type, d.status, d.created_at, d.closed_at, d.partner_id, d.order_id, d.order_ref, d.shipping_ref, d.comment, p.name, p.code FROM docs d LEFT JOIN partners p ON p.id = d.partner_id";
    private const string OrderSelectBase = "SELECT o.id, o.order_ref, o.partner_id, o.due_date, o.status, o.comment, o.created_at, p.name, p.code FROM orders o LEFT JOIN partners p ON p.id = o.partner_id";

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
    uom TEXT,
    base_uom TEXT NOT NULL DEFAULT 'шт',
    default_packaging_id INTEGER
);
CREATE TABLE IF NOT EXISTS uoms (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_uoms_name ON uoms(name);
CREATE TABLE IF NOT EXISTS item_packaging (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id INTEGER NOT NULL,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    factor_to_base REAL NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (item_id) REFERENCES items(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_item_packaging_item_code ON item_packaging(item_id, code);
CREATE INDEX IF NOT EXISTS ix_item_packaging_item ON item_packaging(item_id);
CREATE TABLE IF NOT EXISTS locations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS partners (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    code TEXT,
    created_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_partners_code ON partners(code);
CREATE TABLE IF NOT EXISTS orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_ref TEXT NOT NULL,
    partner_id INTEGER NOT NULL,
    due_date TEXT,
    status TEXT NOT NULL DEFAULT 'ACCEPTED',
    comment TEXT,
    created_at TEXT NOT NULL,
    FOREIGN KEY (partner_id) REFERENCES partners(id)
);
CREATE INDEX IF NOT EXISTS ix_orders_ref ON orders(order_ref);
CREATE INDEX IF NOT EXISTS ix_orders_partner ON orders(partner_id);
CREATE TABLE IF NOT EXISTS order_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    qty_ordered REAL NOT NULL,
    FOREIGN KEY (order_id) REFERENCES orders(id),
    FOREIGN KEY (item_id) REFERENCES items(id)
);
CREATE INDEX IF NOT EXISTS ix_order_lines_order ON order_lines(order_id);
CREATE TABLE IF NOT EXISTS docs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_ref TEXT NOT NULL,
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    closed_at TEXT,
    partner_id INTEGER,
    order_id INTEGER,
    order_ref TEXT,
    shipping_ref TEXT,
    comment TEXT,
    FOREIGN KEY (partner_id) REFERENCES partners(id),
    FOREIGN KEY (order_id) REFERENCES orders(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_docs_ref_type ON docs(doc_ref, type);
CREATE TABLE IF NOT EXISTS doc_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    qty REAL NOT NULL,
    qty_input REAL,
    uom_code TEXT,
    from_location_id INTEGER,
    to_location_id INTEGER,
    from_hu TEXT,
    to_hu TEXT
);
CREATE INDEX IF NOT EXISTS ix_doc_lines_doc ON doc_lines(doc_id);
CREATE TABLE IF NOT EXISTS ledger (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    location_id INTEGER NOT NULL,
    qty_delta REAL NOT NULL,
    hu TEXT
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

        EnsureColumn(connection, "items", "gtin", "TEXT");
        EnsureColumn(connection, "items", "uom", "TEXT");
        EnsureColumn(connection, "items", "base_uom", "TEXT NOT NULL DEFAULT 'шт'");
        EnsureColumn(connection, "items", "default_packaging_id", "INTEGER");
        EnsureColumn(connection, "partners", "created_at", "TEXT");
        EnsureColumn(connection, "docs", "partner_id", "INTEGER");
        EnsureColumn(connection, "docs", "order_id", "INTEGER");
        EnsureColumn(connection, "docs", "order_ref", "TEXT");
        EnsureColumn(connection, "docs", "shipping_ref", "TEXT");
        EnsureColumn(connection, "docs", "comment", "TEXT");
        EnsureColumn(connection, "doc_lines", "qty_input", "REAL");
        EnsureColumn(connection, "doc_lines", "uom_code", "TEXT");
        EnsureColumn(connection, "doc_lines", "from_hu", "TEXT");
        EnsureColumn(connection, "doc_lines", "to_hu", "TEXT");
        EnsureColumn(connection, "ledger", "hu", "TEXT");

        EnsureIndex(connection, "ix_docs_order", "docs(order_id)");
        EnsureIndex(connection, "ix_item_packaging_item_code", "item_packaging(item_id, code)");
        EnsureIndex(connection, "ix_item_packaging_item", "item_packaging(item_id)");
        EnsureIndex(connection, "ix_ledger_item_loc_hu", "ledger(item_id, location_id, hu)");

        BackfillBaseUom(connection);
        BackfillPartnerCreatedAt(connection);
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
            using var command = CreateCommand(connection, "SELECT id, name, barcode, gtin, base_uom, default_packaging_id FROM items WHERE barcode = @barcode OR gtin = @barcode");
            command.Parameters.AddWithValue("@barcode", barcode);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        });
    }

    public Item? FindItemById(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, barcode, gtin, base_uom, default_packaging_id FROM items WHERE id = @id");
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
INSERT INTO items(name, barcode, gtin, base_uom, default_packaging_id)
VALUES(@name, @barcode, @gtin, @base_uom, @default_packaging_id);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@name", item.Name);
            command.Parameters.AddWithValue("@barcode", (object?)item.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("@gtin", (object?)item.Gtin ?? DBNull.Value);
            command.Parameters.AddWithValue("@base_uom", item.BaseUom);
            command.Parameters.AddWithValue("@default_packaging_id", item.DefaultPackagingId.HasValue ? item.DefaultPackagingId.Value : DBNull.Value);
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

    public void UpdateItem(Item item)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE items
SET name = @name,
    barcode = @barcode,
    gtin = @gtin,
    base_uom = @base_uom,
    default_packaging_id = @default_packaging_id
WHERE id = @id;
");
            command.Parameters.AddWithValue("@name", item.Name);
            command.Parameters.AddWithValue("@barcode", (object?)item.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("@gtin", (object?)item.Gtin ?? DBNull.Value);
            command.Parameters.AddWithValue("@base_uom", item.BaseUom);
            command.Parameters.AddWithValue("@default_packaging_id", item.DefaultPackagingId.HasValue ? item.DefaultPackagingId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", item.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteItem(long itemId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM items WHERE id = @id");
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsItemUsed(long itemId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM doc_lines WHERE item_id = @id LIMIT 1");
            command.Parameters.AddWithValue("@id", itemId);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var orderCommand = CreateCommand(connection, "SELECT 1 FROM order_lines WHERE item_id = @id LIMIT 1");
            orderCommand.Parameters.AddWithValue("@id", itemId);
            if (orderCommand.ExecuteScalar() != null)
            {
                return true;
            }

            using var ledgerCommand = CreateCommand(connection, "SELECT 1 FROM ledger WHERE item_id = @id LIMIT 1");
            ledgerCommand.Parameters.AddWithValue("@id", itemId);
            return ledgerCommand.ExecuteScalar() != null;
        });
    }

    public void UpdateItemDefaultPackaging(long itemId, long? packagingId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE items SET default_packaging_id = @packaging_id WHERE id = @id");
            command.Parameters.AddWithValue("@packaging_id", packagingId.HasValue ? packagingId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<ItemPackaging> GetItemPackagings(long itemId, bool includeInactive)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT id, item_id, code, name, factor_to_base, is_active, sort_order
FROM item_packaging
WHERE item_id = @item_id";
            if (!includeInactive)
            {
                sql += " AND is_active = 1";
            }
            sql += " ORDER BY sort_order, name;";

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@item_id", itemId);
            using var reader = command.ExecuteReader();
            var list = new List<ItemPackaging>();
            while (reader.Read())
            {
                list.Add(ReadItemPackaging(reader));
            }

            return list;
        });
    }

    public ItemPackaging? GetItemPackaging(long packagingId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, item_id, code, name, factor_to_base, is_active, sort_order
FROM item_packaging
WHERE id = @id;");
            command.Parameters.AddWithValue("@id", packagingId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemPackaging(reader) : null;
        });
    }

    public ItemPackaging? FindItemPackagingByCode(long itemId, string code)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, item_id, code, name, factor_to_base, is_active, sort_order
FROM item_packaging
WHERE item_id = @item_id AND code = @code;");
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@code", code);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemPackaging(reader) : null;
        });
    }

    public long AddItemPackaging(ItemPackaging packaging)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO item_packaging(item_id, code, name, factor_to_base, is_active, sort_order)
VALUES(@item_id, @code, @name, @factor_to_base, @is_active, @sort_order);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@item_id", packaging.ItemId);
            command.Parameters.AddWithValue("@code", packaging.Code);
            command.Parameters.AddWithValue("@name", packaging.Name);
            command.Parameters.AddWithValue("@factor_to_base", packaging.FactorToBase);
            command.Parameters.AddWithValue("@is_active", packaging.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@sort_order", packaging.SortOrder);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateItemPackaging(ItemPackaging packaging)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE item_packaging
SET item_id = @item_id,
    code = @code,
    name = @name,
    factor_to_base = @factor_to_base,
    is_active = @is_active,
    sort_order = @sort_order
WHERE id = @id;
");
            command.Parameters.AddWithValue("@item_id", packaging.ItemId);
            command.Parameters.AddWithValue("@code", packaging.Code);
            command.Parameters.AddWithValue("@name", packaging.Name);
            command.Parameters.AddWithValue("@factor_to_base", packaging.FactorToBase);
            command.Parameters.AddWithValue("@is_active", packaging.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@sort_order", packaging.SortOrder);
            command.Parameters.AddWithValue("@id", packaging.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeactivateItemPackaging(long packagingId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE item_packaging SET is_active = 0 WHERE id = @id");
            command.Parameters.AddWithValue("@id", packagingId);
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

    public Location? FindLocationById(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, code, name FROM locations WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
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

    public void UpdateLocation(Location location)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE locations
SET code = @code,
    name = @name
WHERE id = @id;
");
            command.Parameters.AddWithValue("@code", location.Code);
            command.Parameters.AddWithValue("@name", location.Name);
            command.Parameters.AddWithValue("@id", location.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteLocation(long locationId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM locations WHERE id = @id");
            command.Parameters.AddWithValue("@id", locationId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsLocationUsed(long locationId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM doc_lines
WHERE from_location_id = @id OR to_location_id = @id
LIMIT 1;
");
            command.Parameters.AddWithValue("@id", locationId);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var ledgerCommand = CreateCommand(connection, "SELECT 1 FROM ledger WHERE location_id = @id LIMIT 1");
            ledgerCommand.Parameters.AddWithValue("@id", locationId);
            return ledgerCommand.ExecuteScalar() != null;
        });
    }

    public IReadOnlyList<Uom> GetUoms()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name FROM uoms ORDER BY name");
            using var reader = command.ExecuteReader();
            var uoms = new List<Uom>();
            while (reader.Read())
            {
                uoms.Add(ReadUom(reader));
            }

            return uoms;
        });
    }

    public long AddUom(Uom uom)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO uoms(name)
VALUES(@name);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@name", uom.Name);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public Partner? GetPartner(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, code, created_at FROM partners WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPartner(reader) : null;
        });
    }

    public Partner? FindPartnerByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, code, created_at FROM partners WHERE code = @code LIMIT 1");
            command.Parameters.AddWithValue("@code", code.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPartner(reader) : null;
        });
    }

    public IReadOnlyList<Partner> GetPartners()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, name, code, created_at FROM partners ORDER BY name");
            using var reader = command.ExecuteReader();
            var partners = new List<Partner>();
            while (reader.Read())
            {
                partners.Add(ReadPartner(reader));
            }

            return partners;
        });
    }

    public long AddPartner(Partner partner)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO partners(name, code, created_at)
VALUES(@name, @code, @created_at);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@name", partner.Name);
            command.Parameters.AddWithValue("@code", (object?)partner.Code ?? DBNull.Value);
            command.Parameters.AddWithValue("@created_at", ToDbDate(partner.CreatedAt));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdatePartner(Partner partner)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE partners
SET name = @name,
    code = @code
WHERE id = @id;
");
            command.Parameters.AddWithValue("@name", partner.Name);
            command.Parameters.AddWithValue("@code", (object?)partner.Code ?? DBNull.Value);
            command.Parameters.AddWithValue("@id", partner.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeletePartner(long partnerId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM partners WHERE id = @id");
            command.Parameters.AddWithValue("@id", partnerId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool IsPartnerUsed(long partnerId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT 1 FROM docs WHERE partner_id = @id LIMIT 1");
            command.Parameters.AddWithValue("@id", partnerId);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }

            using var orderCommand = CreateCommand(connection, "SELECT 1 FROM orders WHERE partner_id = @id LIMIT 1");
            orderCommand.Parameters.AddWithValue("@id", partnerId);
            return orderCommand.ExecuteScalar() != null;
        });
    }

    public Doc? FindDocByRef(string docRef, DocType type)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} WHERE d.doc_ref = @doc_ref AND d.type = @type");
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
            using var command = CreateCommand(connection, $"{DocSelectBase} WHERE d.id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDoc(reader) : null;
        });
    }

    public IReadOnlyList<Doc> GetDocs()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} ORDER BY d.created_at DESC");
            using var reader = command.ExecuteReader();
            var docs = new List<Doc>();
            while (reader.Read())
            {
                docs.Add(ReadDoc(reader));
            }

            return docs;
        });
    }

    public IReadOnlyList<Doc> GetDocsByOrder(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{DocSelectBase} WHERE d.order_id = @order_id ORDER BY d.created_at DESC");
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var docs = new List<Doc>();
            while (reader.Read())
            {
                docs.Add(ReadDoc(reader));
            }

            return docs;
        });
    }

    public int GetMaxDocRefSequence(DocType type, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return 0;
        }

        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT doc_ref FROM docs WHERE type = @type AND doc_ref LIKE @prefix");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(type));
            command.Parameters.AddWithValue("@prefix", $"{prefix}%");
            using var reader = command.ExecuteReader();

            var max = 0;
            while (reader.Read())
            {
                var docRef = reader.GetString(0);
                if (!docRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = docRef.Substring(prefix.Length);
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
                {
                    max = value;
                }
            }

            return max;
        });
    }

    public long AddDoc(Doc doc)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, shipping_ref, comment)
VALUES(@doc_ref, @type, @status, @created_at, @closed_at, @partner_id, @order_id, @order_ref, @shipping_ref, @comment);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(doc.Type));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
            command.Parameters.AddWithValue("@created_at", ToDbDate(doc.CreatedAt));
            command.Parameters.AddWithValue("@closed_at", doc.ClosedAt.HasValue ? ToDbDate(doc.ClosedAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@partner_id", doc.PartnerId.HasValue ? doc.PartnerId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_id", doc.OrderId.HasValue ? doc.OrderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_ref", string.IsNullOrWhiteSpace(doc.OrderRef) ? DBNull.Value : doc.OrderRef);
            command.Parameters.AddWithValue("@shipping_ref", string.IsNullOrWhiteSpace(doc.ShippingRef) ? DBNull.Value : doc.ShippingRef);
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(doc.Comment) ? DBNull.Value : doc.Comment);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<DocLine> GetDocLines(long docId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, doc_id, item_id, qty, qty_input, uom_code, from_location_id, to_location_id, from_hu, to_hu FROM doc_lines WHERE doc_id = @doc_id ORDER BY id");
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
SELECT dl.id, dl.item_id, i.name, i.barcode, dl.qty, dl.qty_input, dl.uom_code, i.base_uom, lf.code, lt.code
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
                    ItemId = reader.GetInt64(1),
                    ItemName = reader.GetString(2),
                    Barcode = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Qty = reader.GetDouble(4),
                    QtyInput = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    UomCode = reader.IsDBNull(6) ? null : reader.GetString(6),
                    BaseUom = reader.IsDBNull(7) ? "шт" : reader.GetString(7),
                    FromLocation = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ToLocation = reader.IsDBNull(9) ? null : reader.GetString(9)
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
INSERT INTO doc_lines(doc_id, item_id, qty, qty_input, uom_code, from_location_id, to_location_id, from_hu, to_hu)
VALUES(@doc_id, @item_id, @qty, @qty_input, @uom_code, @from_location_id, @to_location_id, @from_hu, @to_hu);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@doc_id", line.DocId);
            command.Parameters.AddWithValue("@item_id", line.ItemId);
            command.Parameters.AddWithValue("@qty", line.Qty);
            command.Parameters.AddWithValue("@qty_input", line.QtyInput.HasValue ? line.QtyInput.Value : DBNull.Value);
            command.Parameters.AddWithValue("@uom_code", string.IsNullOrWhiteSpace(line.UomCode) ? DBNull.Value : line.UomCode);
            command.Parameters.AddWithValue("@from_location_id", (object?)line.FromLocationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@to_location_id", (object?)line.ToLocationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@from_hu", string.IsNullOrWhiteSpace(line.FromHu) ? DBNull.Value : line.FromHu);
            command.Parameters.AddWithValue("@to_hu", string.IsNullOrWhiteSpace(line.ToHu) ? DBNull.Value : line.ToHu);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateDocLineQty(long docLineId, double qty, double? qtyInput, string? uomCode)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE doc_lines SET qty = @qty, qty_input = @qty_input, uom_code = @uom_code WHERE id = @id");
            command.Parameters.AddWithValue("@qty", qty);
            command.Parameters.AddWithValue("@qty_input", qtyInput.HasValue ? qtyInput.Value : DBNull.Value);
            command.Parameters.AddWithValue("@uom_code", string.IsNullOrWhiteSpace(uomCode) ? DBNull.Value : uomCode);
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

    public void DeleteDocLines(long docId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM doc_lines WHERE doc_id = @doc_id");
            command.Parameters.AddWithValue("@doc_id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocHeader(long docId, long? partnerId, string? orderRef, string? shippingRef)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET partner_id = @partner_id,
    order_ref = @order_ref,
    shipping_ref = @shipping_ref
WHERE id = @id
");
            command.Parameters.AddWithValue("@partner_id", partnerId.HasValue ? partnerId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_ref", string.IsNullOrWhiteSpace(orderRef) ? DBNull.Value : orderRef);
            command.Parameters.AddWithValue("@shipping_ref", string.IsNullOrWhiteSpace(shippingRef) ? DBNull.Value : shippingRef);
            command.Parameters.AddWithValue("@id", docId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateDocOrder(long docId, long? orderId, string? orderRef)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE docs
SET order_id = @order_id,
    order_ref = @order_ref
WHERE id = @id;
");
            command.Parameters.AddWithValue("@order_id", orderId.HasValue ? orderId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@order_ref", string.IsNullOrWhiteSpace(orderRef) ? DBNull.Value : orderRef);
            command.Parameters.AddWithValue("@id", docId);
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

    public Order? GetOrder(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{OrderSelectBase} WHERE o.id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadOrder(reader) : null;
        });
    }

    public IReadOnlyList<Order> GetOrders()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, $"{OrderSelectBase} ORDER BY o.created_at DESC");
            using var reader = command.ExecuteReader();
            var orders = new List<Order>();
            while (reader.Read())
            {
                orders.Add(ReadOrder(reader));
            }

            return orders;
        });
    }

    public long AddOrder(Order order)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO orders(order_ref, partner_id, due_date, status, comment, created_at)
VALUES(@order_ref, @partner_id, @due_date, @status, @comment, @created_at);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@order_ref", order.OrderRef);
            command.Parameters.AddWithValue("@partner_id", order.PartnerId);
            command.Parameters.AddWithValue("@due_date", order.DueDate.HasValue ? ToDbDateOnly(order.DueDate.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@status", OrderStatusMapper.StatusToString(order.Status));
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(order.Comment) ? DBNull.Value : order.Comment);
            command.Parameters.AddWithValue("@created_at", ToDbDate(order.CreatedAt));
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateOrder(Order order)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE orders
SET order_ref = @order_ref,
    partner_id = @partner_id,
    due_date = @due_date,
    status = @status,
    comment = @comment
WHERE id = @id;
");
            command.Parameters.AddWithValue("@order_ref", order.OrderRef);
            command.Parameters.AddWithValue("@partner_id", order.PartnerId);
            command.Parameters.AddWithValue("@due_date", order.DueDate.HasValue ? ToDbDateOnly(order.DueDate.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@status", OrderStatusMapper.StatusToString(order.Status));
            command.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(order.Comment) ? DBNull.Value : order.Comment);
            command.Parameters.AddWithValue("@id", order.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void UpdateOrderStatus(long orderId, OrderStatus status)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE orders SET status = @status WHERE id = @id");
            command.Parameters.AddWithValue("@status", OrderStatusMapper.StatusToString(status));
            command.Parameters.AddWithValue("@id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<OrderLine> GetOrderLines(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT id, order_id, item_id, qty_ordered FROM order_lines WHERE order_id = @order_id ORDER BY id");
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var lines = new List<OrderLine>();
            while (reader.Read())
            {
                lines.Add(ReadOrderLine(reader));
            }

            return lines;
        });
    }

    public IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT ol.id, ol.order_id, ol.item_id, i.name, ol.qty_ordered
FROM order_lines ol
INNER JOIN items i ON i.id = ol.item_id
WHERE ol.order_id = @order_id
ORDER BY i.name;
");
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var lines = new List<OrderLineView>();
            while (reader.Read())
            {
                lines.Add(new OrderLineView
                {
                    Id = reader.GetInt64(0),
                    OrderId = reader.GetInt64(1),
                    ItemId = reader.GetInt64(2),
                    ItemName = reader.GetString(3),
                    QtyOrdered = reader.GetDouble(4)
                });
            }

            return lines;
        });
    }

    public long AddOrderLine(OrderLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO order_lines(order_id, item_id, qty_ordered)
VALUES(@order_id, @item_id, @qty_ordered);
SELECT last_insert_rowid();
");
            command.Parameters.AddWithValue("@order_id", line.OrderId);
            command.Parameters.AddWithValue("@item_id", line.ItemId);
            command.Parameters.AddWithValue("@qty_ordered", line.QtyOrdered);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void DeleteOrderLines(long orderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM order_lines WHERE order_id = @order_id");
            command.Parameters.AddWithValue("@order_id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteOrder(long orderId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM orders WHERE id = @id");
            command.Parameters.AddWithValue("@id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyDictionary<long, double> GetLedgerTotalsByItem()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "SELECT item_id, COALESCE(SUM(qty_delta), 0) FROM ledger GROUP BY item_id");
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<long, double>();
            while (reader.Read())
            {
                totals[reader.GetInt64(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public IReadOnlyDictionary<long, double> GetShippedTotalsByOrder(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT dl.item_id, COALESCE(SUM(dl.qty), 0)
FROM docs d
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.type = @type AND d.status = @status AND d.order_id = @order_id
GROUP BY dl.item_id;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@order_id", orderId);
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<long, double>();
            while (reader.Read())
            {
                totals[reader.GetInt64(0)] = reader.GetDouble(1);
            }

            return totals;
        });
    }

    public DateTime? GetOrderShippedAt(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT MAX(closed_at)
FROM docs
WHERE type = @type AND status = @status AND order_id = @order_id;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(DocStatus.Closed));
            command.Parameters.AddWithValue("@order_id", orderId);
            var result = command.ExecuteScalar() as string;
            return FromDbDate(result);
        });
    }

    public bool HasOutboundDocs(long orderId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT 1
FROM docs
WHERE type = @type AND order_id = @order_id
LIMIT 1;
");
            command.Parameters.AddWithValue("@type", DocTypeMapper.ToOpString(DocType.Outbound));
            command.Parameters.AddWithValue("@order_id", orderId);
            return command.ExecuteScalar() != null;
        });
    }

    public void AddLedgerEntry(LedgerEntry entry)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu)
VALUES(@ts, @doc_id, @item_id, @location_id, @qty_delta, @hu);
");
            command.Parameters.AddWithValue("@ts", ToDbDate(entry.Timestamp));
            command.Parameters.AddWithValue("@doc_id", entry.DocId);
            command.Parameters.AddWithValue("@item_id", entry.ItemId);
            command.Parameters.AddWithValue("@location_id", entry.LocationId);
            command.Parameters.AddWithValue("@qty_delta", entry.QtyDelta);
            command.Parameters.AddWithValue("@hu", string.IsNullOrWhiteSpace(entry.Hu) ? DBNull.Value : entry.Hu);
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
                    ItemId = reader.GetInt64(0),
                    ItemName = reader.GetString(1),
                    Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
                    LocationCode = reader.GetString(3),
                    Hu = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Qty = reader.GetDouble(5),
                    BaseUom = reader.IsDBNull(6) ? "шт" : reader.GetString(6)
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

    public IReadOnlyList<string?> GetHuCodesByLocation(long locationId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT hu
FROM ledger
WHERE location_id = @location_id
GROUP BY hu
HAVING COALESCE(SUM(qty_delta), 0) > 0
ORDER BY hu;
");
            command.Parameters.AddWithValue("@location_id", locationId);
            using var reader = command.ExecuteReader();
            var list = new List<string?>();
            while (reader.Read())
            {
                list.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }

            return list;
        });
    }

    public IReadOnlyList<string> GetAllHuCodes()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT hu
FROM ledger
WHERE hu IS NOT NULL
GROUP BY hu
ORDER BY hu;
");
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    list.Add(reader.GetString(0));
                }
            }

            return list;
        });
    }

    public IReadOnlyList<Item> GetItemsByLocationAndHu(long locationId, string? huCode)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT i.id, i.name, i.barcode, i.gtin, i.base_uom, i.default_packaging_id
FROM ledger l
INNER JOIN items i ON i.id = l.item_id
WHERE l.location_id = @location_id";
            if (string.IsNullOrWhiteSpace(huCode))
            {
                sql += " AND l.hu IS NULL";
            }
            else
            {
                sql += " AND l.hu = @hu";
            }
            sql += "\nGROUP BY i.id HAVING COALESCE(SUM(l.qty_delta), 0) > 0 ORDER BY i.name;";

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@location_id", locationId);
            if (!string.IsNullOrWhiteSpace(huCode))
            {
                command.Parameters.AddWithValue("@hu", huCode);
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

    public double GetAvailableQty(long itemId, long locationId, string? huCode)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT COALESCE(SUM(qty_delta), 0)
FROM ledger
WHERE item_id = @item_id AND location_id = @location_id";
            if (string.IsNullOrWhiteSpace(huCode))
            {
                sql += " AND hu IS NULL";
            }
            else
            {
                sql += " AND hu = @hu";
            }

            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@location_id", locationId);
            if (!string.IsNullOrWhiteSpace(huCode))
            {
                command.Parameters.AddWithValue("@hu", huCode);
            }
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        });
    }

    public IReadOnlyDictionary<string, double> GetLedgerTotalsByHu()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT hu, COALESCE(SUM(qty_delta), 0)
FROM ledger
WHERE hu IS NOT NULL
GROUP BY hu;
");
            using var reader = command.ExecuteReader();
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                totals[reader.GetString(0)] = reader.GetDouble(1);
            }

            return totals;
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
        var baseUom = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new Item
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
            Gtin = reader.IsDBNull(3) ? null : reader.GetString(3),
            BaseUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom,
            DefaultPackagingId = reader.IsDBNull(5) ? null : reader.GetInt64(5)
        };
    }

    private static ItemPackaging ReadItemPackaging(SqliteDataReader reader)
    {
        return new ItemPackaging
        {
            Id = reader.GetInt64(0),
            ItemId = reader.GetInt64(1),
            Code = reader.GetString(2),
            Name = reader.GetString(3),
            FactorToBase = reader.GetDouble(4),
            IsActive = reader.GetInt64(5) == 1,
            SortOrder = reader.GetInt32(6)
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

    private static Uom ReadUom(SqliteDataReader reader)
    {
        return new Uom
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1)
        };
    }

    private static Partner ReadPartner(SqliteDataReader reader)
    {
        return new Partner
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Code = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = FromDbDate(reader.IsDBNull(3) ? null : reader.GetString(3)) ?? DateTime.MinValue
        };
    }

    private static Doc ReadDoc(SqliteDataReader reader)
    {
        var type = DocTypeMapper.FromOpString(reader.GetString(2)) ?? DocType.Inbound;
        var status = DocTypeMapper.StatusFromString(reader.GetString(3)) ?? DocStatus.Draft;

        long? partnerId = null;
        long? orderId = null;
        string? orderRef = null;
        string? shippingRef = null;
        string? comment = null;
        string? partnerName = null;
        string? partnerCode = null;

        if (reader.FieldCount > 6 && !reader.IsDBNull(6))
        {
            partnerId = reader.GetInt64(6);
        }

        if (reader.FieldCount > 7 && !reader.IsDBNull(7))
        {
            orderId = reader.GetInt64(7);
        }

        if (reader.FieldCount > 8 && !reader.IsDBNull(8))
        {
            orderRef = reader.GetString(8);
        }

        if (reader.FieldCount > 9 && !reader.IsDBNull(9))
        {
            shippingRef = reader.GetString(9);
        }

        if (reader.FieldCount > 10 && !reader.IsDBNull(10))
        {
            comment = reader.GetString(10);
        }

        if (reader.FieldCount > 11 && !reader.IsDBNull(11))
        {
            partnerName = reader.GetString(11);
        }

        if (reader.FieldCount > 12 && !reader.IsDBNull(12))
        {
            partnerCode = reader.GetString(12);
        }

        return new Doc
        {
            Id = reader.GetInt64(0),
            DocRef = reader.GetString(1),
            Type = type,
            Status = status,
            CreatedAt = FromDbDate(reader.GetString(4)) ?? DateTime.MinValue,
            ClosedAt = reader.IsDBNull(5) ? null : FromDbDate(reader.GetString(5)),
            PartnerId = partnerId,
            OrderId = orderId,
            OrderRef = orderRef,
            ShippingRef = shippingRef,
            Comment = comment,
            PartnerName = partnerName,
            PartnerCode = partnerCode
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
            QtyInput = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            UomCode = reader.IsDBNull(5) ? null : reader.GetString(5),
            FromLocationId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            ToLocationId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            FromHu = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null,
            ToHu = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null
        };
    }

    private static Order ReadOrder(SqliteDataReader reader)
    {
        var status = OrderStatusMapper.StatusFromString(reader.GetString(4)) ?? OrderStatus.Accepted;

        var dueDate = reader.IsDBNull(3) ? null : FromDbDate(reader.GetString(3));
        var comment = reader.IsDBNull(5) ? null : reader.GetString(5);
        var partnerName = reader.IsDBNull(7) ? null : reader.GetString(7);
        var partnerCode = reader.IsDBNull(8) ? null : reader.GetString(8);

        return new Order
        {
            Id = reader.GetInt64(0),
            OrderRef = reader.GetString(1),
            PartnerId = reader.GetInt64(2),
            DueDate = dueDate,
            Status = status,
            Comment = comment,
            CreatedAt = FromDbDate(reader.GetString(6)) ?? DateTime.MinValue,
            PartnerName = partnerName,
            PartnerCode = partnerCode
        };
    }

    private static OrderLine ReadOrderLine(SqliteDataReader reader)
    {
        return new OrderLine
        {
            Id = reader.GetInt64(0),
            OrderId = reader.GetInt64(1),
            ItemId = reader.GetInt64(2),
            QtyOrdered = reader.GetDouble(3)
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

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        if (ColumnExists(connection, tableName, columnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        command.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureIndex(SqliteConnection connection, string indexName, string indexDefinition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {indexDefinition};";
        command.ExecuteNonQuery();
    }

    private static void BackfillPartnerCreatedAt(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE partners SET created_at = @created_at WHERE created_at IS NULL OR created_at = '';";
        command.Parameters.AddWithValue("@created_at", ToDbDate(DateTime.Now));
        command.ExecuteNonQuery();
    }

    private static void BackfillBaseUom(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET base_uom = COALESCE(NULLIF(base_uom, ''), NULLIF(uom, ''), 'шт') WHERE base_uom IS NULL OR base_uom = '';";
        command.ExecuteNonQuery();
    }

    private static string BuildItemsQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return "SELECT id, name, barcode, gtin, base_uom, default_packaging_id FROM items ORDER BY name";
        }

        return "SELECT id, name, barcode, gtin, base_uom, default_packaging_id FROM items WHERE name LIKE @search OR barcode LIKE @search ORDER BY name";
    }

    private static string BuildStockQuery(string? search)
    {
        var baseQuery = @"
SELECT i.id, i.name, i.barcode, l.code, led.hu, SUM(led.qty_delta) AS qty, i.base_uom
FROM ledger led
INNER JOIN items i ON i.id = led.item_id
INNER JOIN locations l ON l.id = led.location_id
";

        if (!string.IsNullOrWhiteSpace(search))
        {
            baseQuery += "WHERE i.name LIKE @search OR i.barcode LIKE @search OR l.code LIKE @search\n";
        }

        baseQuery += "GROUP BY i.id, i.name, i.barcode, i.base_uom, l.id, led.hu HAVING qty != 0 ORDER BY i.name, l.code, led.hu";
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

    private static string ToDbDateOnly(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
