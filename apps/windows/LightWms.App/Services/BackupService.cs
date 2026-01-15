using System.Diagnostics;
using System.IO;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LightWms.App;

public sealed class BackupService
{
    private readonly string _dbPath;
    private readonly string _backupsDir;
    private readonly FileLogger _logger;
    private readonly object _sync = new();

    public BackupService(string dbPath, string backupsDir, FileLogger logger)
    {
        _dbPath = dbPath;
        _backupsDir = backupsDir;
        _logger = logger;
    }

    public string CreateBackup(string reason)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_backupsDir);
            if (!File.Exists(_dbPath))
            {
                throw new FileNotFoundException("Файл базы данных не найден.", _dbPath);
            }

            TryCheckpoint();

            var fileName = $"LightWMS_{DateTime.Now:yyyyMMdd_HHmmss}.db";
            var targetPath = Path.Combine(_backupsDir, fileName);
            File.Copy(_dbPath, targetPath, overwrite: false);

            var size = new FileInfo(targetPath).Length;
            _logger.Info($"Backup created ({reason}) path={targetPath} size={size}");
            return targetPath;
        }
    }

    public List<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(_backupsDir))
        {
            return new List<BackupInfo>();
        }

        var files = Directory.GetFiles(_backupsDir, "*.db");
        var list = new List<BackupInfo>(files.Length);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var createdAt = ParseBackupTimestamp(info.Name) ?? info.LastWriteTime;
            list.Add(new BackupInfo
            {
                FileName = info.Name,
                FullPath = info.FullName,
                CreatedAt = createdAt,
                SizeBytes = info.Length
            });
        }

        return list.OrderByDescending(item => item.CreatedAt).ToList();
    }

    public DateTime? GetLastBackupTime()
    {
        return ListBackups().Select(item => item.CreatedAt).FirstOrDefault();
    }

    public void ApplyRetention(int keepLastN)
    {
        if (keepLastN < 1)
        {
            keepLastN = 1;
        }

        var backups = ListBackups();
        if (backups.Count <= keepLastN)
        {
            return;
        }

        foreach (var backup in backups.Skip(keepLastN))
        {
            try
            {
                File.Delete(backup.FullPath);
                _logger.Info($"Backup deleted path={backup.FullPath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Backup delete failed path={backup.FullPath}", ex);
            }
        }
    }

    public void OpenBackupsFolder()
    {
        Directory.CreateDirectory(_backupsDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = _backupsDir,
            UseShellExecute = true
        });
    }

    private void TryCheckpoint()
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Backup checkpoint failed: {ex.Message}");
        }
    }

    private static DateTime? ParseBackupTimestamp(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split('_');
        if (parts.Length < 3)
        {
            return null;
        }

        var stamp = $"{parts[1]}_{parts[2]}";
        if (DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

public sealed class BackupInfo
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public long SizeBytes { get; init; }

    public string SizeDisplay
    {
        get
        {
            if (SizeBytes <= 0)
            {
                return "0 KB";
            }

            var size = SizeBytes / 1024.0;
            if (size < 1024)
            {
                return $"{size:0.#} KB";
            }

            return $"{size / 1024.0:0.#} MB";
        }
    }
}
