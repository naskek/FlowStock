using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using Npgsql;

namespace FlowStock.App;

public sealed class BackupService
{
    private readonly string _connectionString;
    private readonly string _backupsDir;
    private readonly FileLogger _logger;
    private readonly object _sync = new();

    public BackupService(string connectionString, string backupsDir, FileLogger logger)
    {
        _connectionString = connectionString;
        _backupsDir = backupsDir;
        _logger = logger;
    }

    public string CreateBackup(string reason)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_backupsDir);
            var fileName = $"FlowStock_{DateTime.Now:yyyyMMdd_HHmmss}.dump";
            var targetPath = Path.Combine(_backupsDir, fileName);
            RunPgDump(targetPath);

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

        var files = Directory.EnumerateFiles(_backupsDir)
            .Where(file =>
            {
                var extension = Path.GetExtension(file);
                return string.Equals(extension, ".dump", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
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

    private void RunPgDump(string targetPath)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var host = builder.Host ?? string.Empty;
        var database = builder.Database ?? string.Empty;
        var username = string.IsNullOrWhiteSpace(builder.Username) ? "postgres" : builder.Username;
        var password = builder.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException("Postgres connection settings are missing.");
        }

        if (TryRunLocalPgDump(targetPath, host, database, username, password, builder.Port, out var localError, out var notFound))
        {
            return;
        }

        if (!notFound)
        {
            throw new InvalidOperationException(localError ?? "pg_dump failed.");
        }

        if (TryRunDockerPgDump(targetPath, host, database, username, password, out var dockerError))
        {
            return;
        }

        var message = localError ?? dockerError ?? "pg_dump failed.";
        throw new InvalidOperationException(message);
    }

    private bool TryRunLocalPgDump(
        string targetPath,
        string host,
        string database,
        string username,
        string password,
        int port,
        out string? error,
        out bool notFound)
    {
        error = null;
        notFound = false;
        var pgDumpPath = ResolvePgDumpPath();
        var args = new List<string>
        {
            "--format=c",
            "--no-owner",
            "--no-acl",
            $"--host {QuoteArg(host)}",
            $"--port {port}",
            $"--username {QuoteArg(username)}",
            $"--file {QuoteArg(targetPath)}",
            QuoteArg(database)
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = pgDumpPath,
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            startInfo.Environment["PGPASSWORD"] = password;
        }

        try
        {
            RunProcess(startInfo, null);
            return true;
        }
        catch (Win32Exception ex) when (IsFileNotFound(ex))
        {
            notFound = true;
            error = BuildPgDumpNotFoundMessage();
            return false;
        }
        catch (Exception ex)
        {
            error = $"pg_dump failed: {ex.Message}";
            return false;
        }
    }

    private bool TryRunDockerPgDump(
        string targetPath,
        string host,
        string database,
        string username,
        string password,
        out string? error)
    {
        error = null;
        if (!IsLocalHost(host))
        {
            error = "pg_dump не найден. Для удаленного Postgres нужен установленный pg_dump.";
            return false;
        }

        var containerName = Environment.GetEnvironmentVariable("FLOWSTOCK_PG_CONTAINER");
        if (string.IsNullOrWhiteSpace(containerName))
        {
            containerName = "flowstock-pg-dev";
        }

        var args = new List<string> { "exec", "-i" };
        if (!string.IsNullOrWhiteSpace(password))
        {
            args.Add("-e");
            args.Add(QuoteArg($"PGPASSWORD={password}"));
        }
        args.Add(QuoteArg(containerName));
        args.Add("pg_dump");
        args.Add("--format=c");
        args.Add("--no-owner");
        args.Add("--no-acl");
        args.Add($"--host {QuoteArg("127.0.0.1")}");
        args.Add("--port 5432");
        args.Add($"--username {QuoteArg(username)}");
        args.Add(QuoteArg(database));

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            RunProcess(startInfo, targetPath);
            return true;
        }
        catch (Win32Exception ex) when (IsFileNotFound(ex))
        {
            error = BuildPgDumpNotFoundMessage();
            return false;
        }
        catch (Exception ex)
        {
            error = $"pg_dump failed: {ex.Message}";
            return false;
        }
    }

    private static void RunProcess(ProcessStartInfo startInfo, string? stdoutFile)
    {
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        }

        var errorTask = process.StandardError.ReadToEndAsync();
        string? output = null;

        if (string.IsNullOrWhiteSpace(stdoutFile))
        {
            output = process.StandardOutput.ReadToEnd();
        }
        else
        {
            using var fileStream = new FileStream(stdoutFile, FileMode.Create, FileAccess.Write, FileShare.None);
            process.StandardOutput.BaseStream.CopyTo(fileStream);
        }

        process.WaitForExit();
        var error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(details ?? "pg_dump failed.");
        }
    }

    private static string ResolvePgDumpPath()
    {
        var envPath = Environment.GetEnvironmentVariable("FLOWSTOCK_PG_DUMP");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var knownRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PostgreSQL")
        };

        foreach (var root in knownRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var candidates = Directory.EnumerateDirectories(root)
                .Select(dir => Path.Combine(dir, "bin", "pg_dump.exe"))
                .Where(File.Exists)
                .OrderByDescending(path => path)
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        return "pg_dump";
    }

    private static bool IsLocalHost(string host)
    {
        return host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileNotFound(Win32Exception ex)
    {
        return ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3;
    }

    private static string QuoteArg(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string BuildPgDumpNotFoundMessage()
    {
        return "pg_dump не найден. Установите PostgreSQL client tools, добавьте pg_dump в PATH, "
            + "или задайте переменную FLOWSTOCK_PG_DUMP. Для Docker можно задать FLOWSTOCK_PG_CONTAINER "
            + "(по умолчанию flowstock-pg-dev).";
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

