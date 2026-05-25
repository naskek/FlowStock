using FlowStock.Core.Abstractions;
using Npgsql;

namespace FlowStock.App;

public sealed class AdminService
{
    private static readonly HashSet<string> TrackedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs",
        "doc_lines",
        "production_pallets",
        "production_pallet_lines",
        "ledger",
        "orders",
        "order_lines",
        "order_receipt_plan_lines",
        "items",
        "locations",
        "partners",
        "imported_events",
        "import_errors"
    };

    private readonly string _connectionString;
    private readonly BackupService _backups;
    private readonly FileLogger _adminLogger;
    private readonly IDataStore _dataStore;

    public AdminService(string connectionString, IDataStore dataStore, BackupService backups, FileLogger adminLogger)
    {
        _connectionString = connectionString;
        _dataStore = dataStore;
        _backups = backups;
        _adminLogger = adminLogger;
    }

    public Dictionary<string, long> GetTableCounts()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        foreach (var table in GetTrackedTables())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            var count = command.ExecuteScalar();
            result[table] = count == null || count == DBNull.Value ? 0 : Convert.ToInt64(count);
        }

        return result;
    }

    public void DeleteTables(IReadOnlyList<string> tables)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var table in tables)
        {
            if (!TrackedTables.Contains(table))
            {
                throw new InvalidOperationException($"Недопустимая таблица: {table}.");
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        _adminLogger.Info($"selective_reset tables={string.Join(",", tables)}");
    }

    public void ResetMovements()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
UPDATE km_code
SET status = 0,
    receipt_doc_id = NULL,
    receipt_line_id = NULL,
    hu_id = NULL,
    location_id = NULL,
    ship_doc_id = NULL,
    ship_line_id = NULL,
    order_id = NULL;
UPDATE km_code_batch
SET order_id = NULL;
DELETE FROM marking_print_batch_code
WHERE print_batch_id IN (
    SELECT mpb.id
    FROM marking_print_batch mpb
    INNER JOIN marking_order mo ON mo.id = mpb.marking_order_id
    WHERE mo.order_id IS NOT NULL
       OR mo.source_order_id IS NOT NULL
)
   OR marking_code_id IN (
    SELECT mc.id
    FROM marking_code mc
    INNER JOIN marking_order mo ON mo.id = mc.marking_order_id
    WHERE mo.order_id IS NOT NULL
       OR mo.source_order_id IS NOT NULL
);
UPDATE marking_print_batch
SET reprint_of_batch_id = NULL
WHERE reprint_of_batch_id IN (
    SELECT mpb.id
    FROM marking_print_batch mpb
    INNER JOIN marking_order mo ON mo.id = mpb.marking_order_id
    WHERE mo.order_id IS NOT NULL
       OR mo.source_order_id IS NOT NULL
);
DELETE FROM marking_print_batch
WHERE marking_order_id IN (
    SELECT id
    FROM marking_order
    WHERE order_id IS NOT NULL
       OR source_order_id IS NOT NULL
);
DELETE FROM marking_code
WHERE marking_order_id IN (
    SELECT id
    FROM marking_order
    WHERE order_id IS NOT NULL
       OR source_order_id IS NOT NULL
);
UPDATE marking_code_import
SET matched_marking_order_id = NULL
WHERE matched_marking_order_id IN (
    SELECT id
    FROM marking_order
    WHERE order_id IS NOT NULL
       OR source_order_id IS NOT NULL
);
DELETE FROM marking_order
WHERE order_id IS NOT NULL
   OR source_order_id IS NOT NULL;
UPDATE marking_code
SET receipt_doc_id = NULL,
    receipt_line_id = NULL
WHERE receipt_doc_id IS NOT NULL
   OR receipt_line_id IS NOT NULL;
DELETE FROM warehouse_action_bundles;
DELETE FROM order_receipt_plan_lines;
DELETE FROM production_pallet_lines;
DELETE FROM production_pallets;
DELETE FROM ledger;
DELETE FROM doc_lines;
DELETE FROM docs;
DELETE FROM order_lines;
DELETE FROM orders;
DELETE FROM imported_events;
DELETE FROM import_errors;
";
        command.ExecuteNonQuery();
        transaction.Commit();

        _adminLogger.Info("reset_movements success");
    }

    public string FullReset()
    {
        string? archivePath = null;
        try
        {
            archivePath = _backups.CreateBackup("admin_full_reset");
        }
        catch (Exception ex)
        {
            _adminLogger.Warn($"full_reset archive failed: {ex.Message}");
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {string.Join(", ", AllTables)} RESTART IDENTITY CASCADE;";
        command.ExecuteNonQuery();

        _dataStore.Initialize();

        _adminLogger.Info($"full_reset success archived={archivePath ?? "none"}");
        return archivePath ?? string.Empty;
    }

    private static IEnumerable<string> GetTrackedTables()
    {
        return TrackedTables;
    }

    private static readonly string[] AllTables =
    {
        "api_events",
        "api_docs",
        "stock_reservation_lines",
        "tsd_devices",
        "ledger",
        "doc_lines",
        "docs",
        "order_lines",
        "orders",
        "item_packaging",
        "items",
        "uoms",
        "hus",
        "locations",
        "partners",
        "imported_events",
        "import_errors"
    };
}

