using System.IO;
using LightWms.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace LightWms.App;

public sealed class AdminService
{
    private readonly string _dbPath;
    private readonly string _backupsDir;
    private readonly FileLogger _adminLogger;
    private readonly IDataStore _dataStore;

    public AdminService(string dbPath, string backupsDir, IDataStore dataStore, FileLogger adminLogger)
    {
        _dbPath = dbPath;
        _backupsDir = backupsDir;
        _dataStore = dataStore;
        _adminLogger = adminLogger;
    }

    public Dictionary<string, long> GetTableCounts()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
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

    public void ResetMovements()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
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
        Directory.CreateDirectory(_backupsDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var archivePath = Path.Combine(_backupsDir, $"LightWMS_old_{stamp}.db");

        if (File.Exists(_dbPath))
        {
            File.Move(_dbPath, archivePath, overwrite: true);
        }

        MoveIfExists(_dbPath + "-wal", archivePath + "-wal");
        MoveIfExists(_dbPath + "-shm", archivePath + "-shm");

        _dataStore.Initialize();

        _adminLogger.Info($"full_reset success archived={archivePath}");
        return archivePath;
    }

    private static IEnumerable<string> GetTrackedTables()
    {
        return new[]
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
    }

    private static void MoveIfExists(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        File.Move(sourcePath, targetPath, overwrite: true);
    }
}
