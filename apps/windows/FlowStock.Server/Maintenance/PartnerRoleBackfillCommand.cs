using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace FlowStock.Server.Maintenance;

public static class PartnerRoleBackfillCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static bool TryRun(string[] args, string postgresConnectionString, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2
            || !string.Equals(args[0], "maintenance", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "partner-role-backfill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apply = false;
        string? confirm = null;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    apply = false;
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--confirm" when i + 1 < args.Length:
                    confirm = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return true;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    exitCode = 2;
                    return true;
            }
        }

        if (apply && !string.Equals(confirm, "APPLY", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Apply requires --confirm APPLY and a fresh backup.");
            exitCode = 2;
            return true;
        }

        try
        {
            exitCode = Run(postgresConnectionString, apply);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Partner-role backfill failed.");
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    internal static int Run(string postgresConnectionString, bool apply, string? legacyPath = null)
    {
        legacyPath ??= Path.Combine(ServerPaths.BaseDir, "partner_statuses.json");
        var legacy = LoadLegacy(legacyPath, out var malformedError);
        if (malformedError != null)
        {
            Console.Error.WriteLine($"Malformed legacy partner roles: {malformedError}");
            return 2;
        }

        using var connection = new NpgsqlConnection(postgresConnectionString);
        connection.Open();
        using var transaction = apply ? connection.BeginTransaction() : null;
        var rows = new List<(long Id, string? Role)>();
        using (var list = new NpgsqlCommand(
                   "SELECT id, partner_role FROM partners ORDER BY id;",
                   connection,
                   transaction))
        using (var reader = list.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        var ids = rows.Select(row => row.Id).ToHashSet();
        var orphans = legacy.Keys.Where(id => !ids.Contains(id)).OrderBy(id => id).ToArray();
        var alreadyFilled = rows.Count(row => !string.IsNullOrWhiteSpace(row.Role));
        var explicitCount = rows.Count(row => row.Role == null && legacy.ContainsKey(row.Id));
        var defaultBothCount = rows.Count(row => row.Role == null && !legacy.ContainsKey(row.Id));

        Console.WriteLine($"Partner-role backfill mode: {(apply ? "APPLY" : "DRY-RUN")}");
        Console.WriteLine($"Partners: {rows.Count}");
        Console.WriteLine($"Already filled: {alreadyFilled}");
        Console.WriteLine($"Explicit legacy roles to import: {explicitCount}");
        Console.WriteLine($"Default BOTH to apply: {defaultBothCount}");
        Console.WriteLine($"Legacy orphan ids: {(orphans.Length == 0 ? "none" : string.Join(',', orphans))}");
        Console.WriteLine($"NULL before: {rows.Count - alreadyFilled}");

        if (!apply)
        {
            return 0;
        }

        foreach (var row in rows.Where(row => row.Role == null))
        {
            var role = legacy.TryGetValue(row.Id, out var legacyRole)
                ? NormalizeRole(legacyRole)
                : "BOTH";
            using var update = new NpgsqlCommand(@"
UPDATE partners
SET partner_role = @partner_role
WHERE id = @id
  AND partner_role IS NULL;", connection, transaction);
            update.Parameters.AddWithValue("@partner_role", role);
            update.Parameters.AddWithValue("@id", row.Id);
            update.ExecuteNonQuery();
        }

        using var verify = new NpgsqlCommand(
            "SELECT COUNT(*) FROM partners WHERE partner_role IS NULL;",
            connection,
            transaction);
        var nullAfter = Convert.ToInt64(verify.ExecuteScalar() ?? 0L);
        Console.WriteLine($"NULL after: {nullAfter}");
        if (nullAfter != 0)
        {
            transaction!.Rollback();
            Console.Error.WriteLine("Partner-role backfill left NULL rows; transaction rolled back.");
            return 3;
        }

        transaction!.Commit();
        return 0;
    }

    private static IReadOnlyDictionary<long, FlowStockPartnerRole> LoadLegacy(
        string path,
        out string? malformedError)
    {
        malformedError = null;
        if (!File.Exists(path))
        {
            return new Dictionary<long, FlowStockPartnerRole>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<long, FlowStockPartnerRole>>(
                       File.ReadAllText(path),
                       JsonOptions)
                   ?? new Dictionary<long, FlowStockPartnerRole>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            malformedError = ex.Message;
            return new Dictionary<long, FlowStockPartnerRole>();
        }
    }

    private static string NormalizeRole(FlowStockPartnerRole role) => role switch
    {
        FlowStockPartnerRole.Supplier => "SUPPLIER",
        FlowStockPartnerRole.Client => "CLIENT",
        FlowStockPartnerRole.Both => "BOTH",
        _ => throw new InvalidDataException($"Unknown legacy partner role value: {role}.")
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  FlowStock.Server maintenance partner-role-backfill --dry-run");
        Console.WriteLine("  FlowStock.Server maintenance partner-role-backfill --apply --confirm APPLY");
    }
}
