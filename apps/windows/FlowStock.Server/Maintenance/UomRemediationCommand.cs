using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace FlowStock.Server.Maintenance;

public static class UomRemediationCommand
{
    private const string LegacyToken = "шт";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryRun(string[] args, string postgresConnectionString, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2
            || !string.Equals(args[0], "maintenance", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "uom-remediation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apply = false;
        string? confirm = null;
        string? planPath = null;
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
                case "--plan" when i + 1 < args.Length:
                    planPath = args[++i];
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

        if (apply && string.IsNullOrWhiteSpace(planPath))
        {
            Console.Error.WriteLine("Apply requires --plan <path>.");
            exitCode = 2;
            return true;
        }

        if (apply && !string.Equals(confirm, "APPLY", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Apply requires --confirm APPLY and a fresh backup.");
            exitCode = 2;
            return true;
        }

        try
        {
            exitCode = Run(postgresConnectionString, apply, planPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UOM remediation failed; database changes were rolled back.");
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
        }

        return true;
    }

    internal static int Run(string connectionString, bool apply, string? planPath = null)
    {
        var plan = LoadPlan(planPath);
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        if (plan == null)
        {
            var inventory = ReadInventory(connection, null);
            PrintInventory(inventory, "DRY-RUN");
            return inventory.BlockingFindings == 0 ? 0 : 3;
        }

        using var transaction = connection.BeginTransaction();
        ApplyPlan(connection, transaction, plan);
        var result = ReadInventory(connection, transaction);
        PrintInventory(result, apply ? "APPLY" : "DRY-RUN WITH PLAN");
        if (result.BlockingFindings != 0)
        {
            transaction.Rollback();
            Console.Error.WriteLine("Plan leaves blocking UOM findings; transaction rolled back.");
            return 3;
        }

        if (apply)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
            Console.WriteLine("Dry-run simulation rolled back; database was not changed.");
        }

        return 0;
    }

    private static UomRemediationPlan? LoadPlan(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("UOM remediation plan not found.", path);
        }

        try
        {
            return JsonSerializer.Deserialize<UomRemediationPlan>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("UOM remediation plan is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("UOM remediation plan is malformed.", ex);
        }
    }

    private static void ApplyPlan(NpgsqlConnection connection, NpgsqlTransaction transaction, UomRemediationPlan plan)
    {
        foreach (var operation in plan.Rename ?? [])
        {
            var current = LockUom(connection, transaction, operation.UomId)
                          ?? throw new InvalidDataException($"UOM {operation.UomId} not found for rename.");
            if (IsLegacy(current))
            {
                throw new InvalidDataException("Existing master UOM 'шт' overlap cannot be renamed by this rollout.");
            }
            var target = ValidateMasterName(operation.NewName);
            UpdateReferences(connection, transaction, current, target);
            UpdateUomName(connection, transaction, operation.UomId, target);
        }

        foreach (var operation in plan.Merge ?? [])
        {
            var ids = (operation.SourceUomIds ?? []).Append(operation.TargetUomId).Distinct().OrderBy(id => id).ToArray();
            var locked = ids.ToDictionary(id => id, id => LockUom(connection, transaction, id));
            var currentTarget = locked[operation.TargetUomId]
                                ?? throw new InvalidDataException($"Target UOM {operation.TargetUomId} not found for merge.");
            if (locked.Values.Any(value => value != null && IsLegacy(value)))
            {
                throw new InvalidDataException("Existing master UOM 'шт' overlap cannot participate in merge.");
            }
            var targetName = ValidateMasterName(operation.TargetName ?? currentTarget);
            foreach (var sourceId in operation.SourceUomIds ?? [])
            {
                if (sourceId == operation.TargetUomId)
                {
                    throw new InvalidDataException("Merge source cannot equal target UOM.");
                }

                var sourceName = locked.GetValueOrDefault(sourceId);
                if (sourceName != null)
                {
                    UpdateReferences(connection, transaction, sourceName, targetName);
                }
            }

            UpdateReferences(connection, transaction, currentTarget, targetName);
            DeleteUoms(connection, transaction, (operation.SourceUomIds ?? []).Where(id => id != operation.TargetUomId).ToArray());
            UpdateUomName(connection, transaction, operation.TargetUomId, targetName);
        }

        foreach (var operation in plan.MapOrphan ?? [])
        {
            var source = ValidateSourceValue(operation.SourceValue);
            var target = LockUom(connection, transaction, operation.TargetUomId)
                         ?? throw new InvalidDataException($"Target UOM {operation.TargetUomId} not found for orphan mapping.");
            if (IsLegacy(target))
            {
                throw new InvalidDataException("Existing master UOM 'шт' overlap cannot be a remediation target.");
            }
            UpdateReferences(connection, transaction, source, target);
        }

        foreach (var operation in plan.CreateAndMapOrphan ?? [])
        {
            var source = ValidateSourceValue(operation.SourceValue);
            var targetName = ValidateMasterName(operation.NewUomName);
            var targetId = FindUomId(connection, transaction, targetName);
            if (targetId == null)
            {
                using var insert = new NpgsqlCommand("INSERT INTO uoms(name) VALUES(@name) RETURNING id;", connection, transaction);
                insert.Parameters.AddWithValue("@name", targetName);
                targetId = Convert.ToInt64(insert.ExecuteScalar());
            }

            _ = LockUom(connection, transaction, targetId.Value);
            UpdateReferences(connection, transaction, source, targetName);
        }
    }

    private static string? LockUom(NpgsqlConnection connection, NpgsqlTransaction transaction, long id)
    {
        using var command = new NpgsqlCommand("SELECT name FROM uoms WHERE id = @id FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar() as string;
    }

    private static long? FindUomId(NpgsqlConnection connection, NpgsqlTransaction transaction, string name)
    {
        using var command = new NpgsqlCommand(
            "SELECT id FROM uoms WHERE LOWER(BTRIM(name)) = LOWER(BTRIM(@name)) ORDER BY id LIMIT 1 FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@name", name);
        var value = command.ExecuteScalar();
        return value == null ? null : Convert.ToInt64(value);
    }

    private static void UpdateReferences(NpgsqlConnection connection, NpgsqlTransaction transaction, string source, string target)
    {
        using var command = new NpgsqlCommand(@"
UPDATE items
SET base_uom = @target
WHERE LOWER(BTRIM(base_uom)) = LOWER(BTRIM(@source));", connection, transaction);
        command.Parameters.AddWithValue("@source", source);
        command.Parameters.AddWithValue("@target", target);
        command.ExecuteNonQuery();
    }

    private static void UpdateUomName(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, string name)
    {
        using var command = new NpgsqlCommand("UPDATE uoms SET name = @name WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.ExecuteNonQuery();
    }

    private static void DeleteUoms(NpgsqlConnection connection, NpgsqlTransaction transaction, long[] ids)
    {
        if (ids.Length == 0) return;
        using var command = new NpgsqlCommand("DELETE FROM uoms WHERE id = ANY(@ids);", connection, transaction);
        command.Parameters.AddWithValue("@ids", ids);
        command.ExecuteNonQuery();
    }

    private static string ValidateMasterName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Master UOM name cannot be blank.");
        }

        var normalized = value.Trim();
        if (IsLegacy(normalized))
        {
            throw new InvalidDataException("Master UOM name 'шт' is reserved for legacy compatibility.");
        }

        return normalized;
    }

    private static string ValidateSourceValue(string? value)
    {
        if (value == null)
        {
            throw new InvalidDataException("Orphan source value must be specified explicitly; use an empty string for blank item references.");
        }

        var normalized = value.Trim();
        if (IsLegacy(normalized))
        {
            throw new InvalidDataException("Legacy UOM 'шт' cannot be remediated or mapped.");
        }

        return normalized;
    }

    private static UomInventory ReadInventory(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        var blankMasters = QueryRows(connection, transaction, @"
SELECT u.id::text || ':' || COALESCE(NULLIF(u.name, ''), '<blank>') || ':items=' ||
       COALESCE(STRING_AGG(i.id::text, ',' ORDER BY i.id), 'none')
FROM uoms u
LEFT JOIN items i ON LOWER(BTRIM(i.base_uom)) = LOWER(BTRIM(u.name))
WHERE BTRIM(u.name) = ''
GROUP BY u.id, u.name
ORDER BY u.id;");
        var duplicates = QueryRows(connection, transaction, @"
SELECT LOWER(BTRIM(name)) || ':uoms=' || STRING_AGG(id::text, ',' ORDER BY id)
FROM uoms
GROUP BY LOWER(BTRIM(name))
HAVING COUNT(*) > 1
ORDER BY LOWER(BTRIM(name));");
        var blankItems = QueryRows(connection, transaction, @"
SELECT id::text
FROM items
WHERE BTRIM(base_uom) = ''
ORDER BY id;");
        var orphans = QueryRows(connection, transaction, @"
SELECT LOWER(BTRIM(i.base_uom)) || ':items=' || STRING_AGG(i.id::text, ',' ORDER BY i.id)
FROM items i
WHERE BTRIM(i.base_uom) <> ''
  AND LOWER(BTRIM(i.base_uom)) <> 'шт'
  AND NOT EXISTS (
      SELECT 1 FROM uoms u
      WHERE LOWER(BTRIM(u.name)) = LOWER(BTRIM(i.base_uom)))
GROUP BY LOWER(BTRIM(i.base_uom))
ORDER BY LOWER(BTRIM(i.base_uom));");
        var legacyItems = QueryRows(connection, transaction, @"
SELECT id::text
FROM items
WHERE LOWER(BTRIM(base_uom)) = 'шт'
ORDER BY id;");
        var overlaps = QueryRows(connection, transaction, @"
SELECT id::text || ':' || name
FROM uoms
WHERE LOWER(BTRIM(name)) = 'шт'
ORDER BY id;");
        return new UomInventory(blankMasters, duplicates, blankItems, orphans, legacyItems, overlaps);
    }

    private static string[] QueryRows(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void PrintInventory(UomInventory inventory, string mode)
    {
        Console.WriteLine($"UOM remediation mode: {mode}");
        PrintRows("Blank master rows", inventory.BlankMasters);
        PrintRows("Normalized duplicate groups", inventory.Duplicates);
        PrintRows("Blank item references", inventory.BlankItems);
        PrintRows("Orphan item references", inventory.Orphans);
        PrintRows("Allowed legacy шт item ids", inventory.LegacyItems);
        PrintRows("Reserved master шт overlaps", inventory.LegacyMasterOverlaps);
        Console.WriteLine($"Blocking findings: {inventory.BlockingFindings}");
    }

    private static void PrintRows(string label, string[] values) =>
        Console.WriteLine($"{label}: {(values.Length == 0 ? "none" : string.Join("; ", values))}");

    private static bool IsLegacy(string value) =>
        string.Equals(value.Trim(), LegacyToken, StringComparison.OrdinalIgnoreCase);

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  FlowStock.Server maintenance uom-remediation --dry-run");
        Console.WriteLine("  FlowStock.Server maintenance uom-remediation --plan <path> --dry-run");
        Console.WriteLine("  FlowStock.Server maintenance uom-remediation --plan <path> --apply --confirm APPLY");
    }

    private sealed record UomInventory(
        string[] BlankMasters,
        string[] Duplicates,
        string[] BlankItems,
        string[] Orphans,
        string[] LegacyItems,
        string[] LegacyMasterOverlaps)
    {
        public int BlockingFindings => BlankMasters.Length + Duplicates.Length + BlankItems.Length + Orphans.Length;
    }

    private sealed class UomRemediationPlan
    {
        [JsonPropertyName("rename")]
        public RenameOperation[]? Rename { get; init; }

        [JsonPropertyName("merge")]
        public MergeOperation[]? Merge { get; init; }

        [JsonPropertyName("map_orphan")]
        public MapOrphanOperation[]? MapOrphan { get; init; }

        [JsonPropertyName("create_and_map_orphan")]
        public CreateAndMapOrphanOperation[]? CreateAndMapOrphan { get; init; }
    }

    private sealed class RenameOperation
    {
        [JsonPropertyName("uom_id")]
        public long UomId { get; init; }

        [JsonPropertyName("new_name")]
        public string? NewName { get; init; }
    }

    private sealed class MergeOperation
    {
        [JsonPropertyName("target_uom_id")]
        public long TargetUomId { get; init; }

        [JsonPropertyName("source_uom_ids")]
        public long[]? SourceUomIds { get; init; }

        [JsonPropertyName("target_name")]
        public string? TargetName { get; init; }
    }

    private sealed class MapOrphanOperation
    {
        [JsonPropertyName("source_value")]
        public string? SourceValue { get; init; }

        [JsonPropertyName("target_uom_id")]
        public long TargetUomId { get; init; }
    }

    private sealed class CreateAndMapOrphanOperation
    {
        [JsonPropertyName("source_value")]
        public string? SourceValue { get; init; }

        [JsonPropertyName("new_uom_name")]
        public string? NewUomName { get; init; }
    }
}
