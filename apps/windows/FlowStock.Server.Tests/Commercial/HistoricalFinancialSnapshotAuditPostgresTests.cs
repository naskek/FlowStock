using System.Diagnostics;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

public sealed class HistoricalFinancialSnapshotAuditPostgresTests
{
    [Fact]
    public async Task Dry_run_executes_against_current_postgres_schema()
    {
        await ExecuteSqlFileAsync(
            "historical-financial-snapshot-backfill-dry-run.sql");
    }

    [Fact]
    public async Task Production_preflight_executes_against_current_postgres_schema()
    {
        await ExecuteSqlFileAsync(
            "historical-financial-snapshot-backfill-production-preflight.sql");
    }

    [Fact]
    public async Task Production_apply_rejects_mismatched_guards_before_update()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var scriptPath = FindRepoFile(
            "tools",
            "audit",
            "historical-financial-snapshot-backfill-production-apply.sql");

        var startInfo = new ProcessStartInfo
        {
            FileName = "psql",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PGPASSWORD"] = builder.Password;
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(builder.Host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(builder.Port.ToString());
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(builder.Username);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(builder.Database);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("ON_ERROR_STOP=1");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("CONFIRM_PRODUCTION_APPLY=APPLY_HISTORICAL_SNAPSHOT_BACKFILL");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("CONFIRM_FRESH_BACKUP=YES");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("EXPECTED_CANDIDATE_COUNT=9223372036854775807");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("EXPECTED_CANDIDATE_QUANTITY=9223372036854775807");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("EXPECTED_RECONSTRUCTED_GROSS=1");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("EXPECTED_CANDIDATE_FINGERPRINT=deliberately-wrong");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("EXPECTED_BLOCKED_COUNT=9223372036854775807");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("EXPECTED_BLOCKED_QUANTITY=9223372036854775807");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start psql.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);

        Assert.Equal(4, process.ExitCode);
        Assert.Contains(
            "production candidate set differs from reviewed preflight",
            output,
            StringComparison.Ordinal);
        Assert.Contains("ROLLBACK", output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExecuteSqlFileAsync(string fileName)
    {
        var sql = File.ReadAllText(FindRepoFile(
            "tools",
            "audit",
            fileName));

        await using var connection = new NpgsqlConnection(ResolveRequiredPostgresTestConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;

        await command.ExecuteNonQueryAsync();
    }

    private static string ResolveRequiredPostgresTestConnectionString()
    {
        foreach (var key in new[]
                 {
                     "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
                     "FLOWSTOCK_POSTGRES_CONNECTION",
                     "POSTGRES_CONNECTION_STRING"
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        throw new InvalidOperationException(
            "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
