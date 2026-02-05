using FlowStock.Core.Abstractions;
using Npgsql;

namespace FlowStock.App;

public sealed class AdminService
{
    private static readonly HashSet<string> TrackedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs",
        "doc_lines",
        "ledger",
        "orders",
        "order_lines",
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

