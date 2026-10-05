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
