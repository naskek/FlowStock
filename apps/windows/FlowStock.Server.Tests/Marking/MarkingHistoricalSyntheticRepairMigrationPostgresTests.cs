using System.Globalization;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingHistoricalSyntheticRepairMigrationPostgresTests
{
    [Theory]
    [InlineData("Applied")]
    [InlineData("Reserved")]
    public async Task V0037_ReclassifiesExactHistoricalSyntheticCode(string status)
    {
        await WithTemporaryDatabaseAsync(async connection =>
        {
            await ApplyMigrationsThroughAsync(connection, 36);
            await SeedMarkingEnvelopeAsync(connection);

            await using (var insert = new NpgsqlCommand("""
INSERT INTO marking_code(
    id, code, code_hash, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES(
    'a3700000-0000-0000-0000-000000000201',
    'TEMP-CHZ-V0037-EXACT',
    'V0037-EXACT-HASH',
    'a3700000-0000-0000-0000-000000000001',
    'a3700000-0000-0000-0000-000000000101',
    @status,
    'HistoricalUnknown',
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z');
""", connection))
            {
                insert.Parameters.AddWithValue("@status", status);
                await insert.ExecuteNonQueryAsync();
            }

            await ApplyMigrationAsync(connection, FindMigration(37));

            Assert.Equal(
                "LegacySynthetic",
                await ExecuteScalarStringAsync(
                    connection,
                    "SELECT origin FROM marking_code WHERE code = 'TEMP-CHZ-V0037-EXACT';"));
        });
    }

    [Theory]
    [InlineData("NOT-TEMP-V0037", "temporary-chz-export", "<temporary-chz-export>", "Reserved")]
    [InlineData("TEMP-CHZ-V0037-WRONG-SOURCE", "csv", "<temporary-chz-export>", "Reserved")]
    [InlineData("TEMP-CHZ-V0037-WRONG-PATH", "temporary-chz-export", "v0037.xlsx", "Reserved")]
    [InlineData("TEMP-CHZ-V0037-QUARANTINED", "temporary-chz-export", "<temporary-chz-export>", "Quarantined")]
    public async Task V0037_LeavesIncompleteOrQuarantinedCandidateHistoricalUnknown(
        string code,
        string sourceType,
        string storagePath,
        string status)
    {
        await WithTemporaryDatabaseAsync(async connection =>
        {
            await ApplyMigrationsThroughAsync(connection, 36);
            await SeedMarkingEnvelopeAsync(connection);

            await using (var updateImport = new NpgsqlCommand("""
UPDATE marking_code_import
SET source_type = @source_type,
    storage_path = @storage_path
WHERE id = 'a3700000-0000-0000-0000-000000000101';
""", connection))
            {
                updateImport.Parameters.AddWithValue("@source_type", sourceType);
                updateImport.Parameters.AddWithValue("@storage_path", storagePath);
                await updateImport.ExecuteNonQueryAsync();
            }

            await using (var insert = new NpgsqlCommand("""
INSERT INTO marking_code(
    id, code, code_hash, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES(
    'a3700000-0000-0000-0000-000000000202',
    @code,
    'V0037-INCOMPLETE-HASH',
    'a3700000-0000-0000-0000-000000000001',
    'a3700000-0000-0000-0000-000000000101',
    @status,
    'HistoricalUnknown',
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z');
""", connection))
            {
                insert.Parameters.AddWithValue("@code", code);
                insert.Parameters.AddWithValue("@status", status);
                await insert.ExecuteNonQueryAsync();
            }

            await ApplyMigrationAsync(connection, FindMigration(37));

            Assert.Equal(
                "HistoricalUnknown",
                await ExecuteScalarStringAsync(
                    connection,
                    "SELECT origin FROM marking_code WHERE id = 'a3700000-0000-0000-0000-000000000202';"));
        });
    }

    [Fact]
    public async Task V0037_LeavesDanglingImportCandidateHistoricalUnknown()
    {
        await WithTemporaryDatabaseAsync(async connection =>
        {
            await ApplyMigrationsThroughAsync(connection, 36);
            await SeedMarkingEnvelopeAsync(connection);
            await ExecuteAsync(connection, """
SET session_replication_role = replica;
INSERT INTO marking_code(
    id, code, code_hash, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES(
    'a3700000-0000-0000-0000-000000000203',
    'TEMP-CHZ-V0037-DANGLING-IMPORT',
    'V0037-DANGLING-HASH',
    'a3700000-0000-0000-0000-000000000001',
    'a3700000-0000-0000-0000-000000000199',
    'Reserved',
    'HistoricalUnknown',
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z');
SET session_replication_role = origin;
""");

            await ApplyMigrationAsync(connection, FindMigration(37));

            Assert.Equal(
                "HistoricalUnknown",
                await ExecuteScalarStringAsync(
                    connection,
                    "SELECT origin FROM marking_code WHERE id = 'a3700000-0000-0000-0000-000000000203';"));
        });
    }

    [Fact]
    public async Task V0037_ChangesOnlyExactOriginsAndUpdatesExistingPreflightClassification()
    {
        await WithTemporaryDatabaseAsync(async connection =>
        {
            await ApplyMigrationsThroughAsync(connection, 36);
            // The current runtime reads the additive V0038 applicability flag. This test still
            // isolates V0037's UPDATE, so expose only the neutral column rather than applying V0038.
            await ExecuteAsync(connection,
                "ALTER TABLE items ADD COLUMN chz_marking_exempt BOOLEAN NOT NULL DEFAULT FALSE;");
            await SeedPreflightScenarioAsync(connection);

            var tablesThatMustNotChange = new[]
            {
                "marking_order",
                "marking_code_import",
                "ledger",
                "docs",
                "doc_lines",
                "hus",
                "production_pallets",
                "production_pallet_lines",
                "marking_settings",
                "marking_production_subject",
                "marking_subject_ownership_audit",
                "marking_request_scope",
                "marking_request_scope_consumption",
                "marking_import_file",
                "marking_import_batch_request",
                "marking_operational_coverage",
                "marking_operational_coverage_consumption",
                "marking_operational_coverage_import_lineage",
                "marking_synthetic_legacy_allowlist",
                "marking_synthetic_legacy_allowlist_subject",
                "marking_grandfather_operational_allowance",
                "marking_ready_hu_fact",
                "marking_ready_hu_fact_lineage",
                "marking_ready_hu_grandfather_lineage"
            };
            var beforeFingerprints = await ReadTableFingerprintsAsync(connection, tablesThatMustNotChange);
            var markingCodesExceptOriginBefore = await ReadMarkingCodesExceptOriginFingerprintAsync(connection);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store);
            var before = preflight.Run(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
            var beforeAgain = preflight.Run(new DateTime(2026, 8, 24, 12, 1, 0, DateTimeKind.Utc));

            Assert.Equal(before.Hash, beforeAgain.Hash);
            Assert.Contains(before.Entries, entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.EndsWith("count=6", StringComparison.Ordinal));

            Assert.Equal(2, await ExecuteMigrationAsync(connection, FindMigration(37)));

            var origins = await ReadOriginsByCodeAsync(connection);
            Assert.Equal("LegacySynthetic", origins["TEMP-CHZ-V0037-APPLIED"]);
            Assert.Equal("LegacySynthetic", origins["TEMP-CHZ-V0037-RESERVED"]);
            Assert.Equal("HistoricalUnknown", origins["NOT-TEMP-V0037-MATRIX"]);
            Assert.Equal("HistoricalUnknown", origins["TEMP-CHZ-V0037-MATRIX-WRONG-SOURCE"]);
            Assert.Equal("HistoricalUnknown", origins["TEMP-CHZ-V0037-MATRIX-WRONG-PATH"]);
            Assert.Equal("HistoricalUnknown", origins["TEMP-CHZ-V0037-MATRIX-QUARANTINED"]);

            Assert.Equal(
                markingCodesExceptOriginBefore,
                await ReadMarkingCodesExceptOriginFingerprintAsync(connection));
            Assert.Equal(
                beforeFingerprints,
                await ReadTableFingerprintsAsync(connection, tablesThatMustNotChange));

            var after = preflight.Run(new DateTime(2026, 8, 24, 12, 2, 0, DateTimeKind.Utc));
            Assert.NotEqual(before.Hash, after.Hash);
            Assert.Contains(after.Entries, entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.EndsWith("count=4", StringComparison.Ordinal));
            Assert.Contains(after.Entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_SYNTHETIC_PRESENT"
                && entry.Level == "warning"
                // V0038 treats only Applied LegacySynthetic as quantitative evidence;
                // the repaired Reserved row remains non-evidence audit history.
                && entry.LegacySyntheticQty == 1);

            Assert.Equal(0, await ExecuteMigrationAsync(connection, FindMigration(37)));
            var afterRerun = preflight.Run(new DateTime(2026, 8, 24, 12, 3, 0, DateTimeKind.Utc));
            Assert.Equal(after.Hash, afterRerun.Hash);
        });
    }

    [Fact]
    public async Task V0037_RepairsRowSkippedWhenV0027SeesPreexistingNonNullOrigin()
    {
        await WithTemporaryDatabaseAsync(async connection =>
        {
            await ApplyMigrationsThroughAsync(connection, 26);
            await ExecuteAsync(connection, """
ALTER TABLE marking_code
ADD COLUMN origin TEXT NOT NULL DEFAULT 'HistoricalUnknown';
""");
            await SeedMarkingEnvelopeAsync(connection);
            await ExecuteAsync(connection, """
INSERT INTO marking_code(
    id, code, code_hash, marking_order_id, import_id, status, created_at, updated_at)
VALUES(
    'a3720000-0000-0000-0000-000000000201',
    'TEMP-CHZ-V0037-PREEXISTING-ORIGIN',
    'V0037-PREEXISTING-ORIGIN-HASH',
    'a3700000-0000-0000-0000-000000000001',
    'a3700000-0000-0000-0000-000000000101',
    'Reserved',
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z');
""");

            await ApplyMigrationsAsync(connection, 27, 36);

            Assert.Equal(
                "HistoricalUnknown",
                await ExecuteScalarStringAsync(
                    connection,
                    "SELECT origin FROM marking_code WHERE id = 'a3720000-0000-0000-0000-000000000201';"));

            await ApplyMigrationAsync(connection, FindMigration(37));

            Assert.Equal(
                "LegacySynthetic",
                await ExecuteScalarStringAsync(
                    connection,
                    "SELECT origin FROM marking_code WHERE id = 'a3720000-0000-0000-0000-000000000201';"));
        });
    }

    [Fact]
    public async Task V0001ThroughV0037_AppliesCleanlyToEmptyDatabase()
    {
        await WithTemporaryDatabaseAsync(async connection =>
        {
            await ApplyMigrationsThroughAsync(connection, 37);

            Assert.Equal(
                "0",
                await ExecuteScalarStringAsync(connection, "SELECT COUNT(*) FROM marking_code;"));
            Assert.Equal(
                "'HistoricalUnknown'::text",
                await ExecuteScalarStringAsync(connection, """
SELECT column_default
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name = 'marking_code'
  AND column_name = 'origin';
"""));
        });
    }

    private static async Task SeedMarkingEnvelopeAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, """
INSERT INTO marking_order(
    id, requested_quantity, request_number, status, source_type, created_at, updated_at)
VALUES(
    'a3700000-0000-0000-0000-000000000001',
    1,
    'V0037-REQUEST',
    'Printed',
    'PRODUCTION_ORDER',
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z');

INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, imported_rows, valid_code_rows,
    created_at, processed_at)
VALUES(
    'a3700000-0000-0000-0000-000000000101',
    'v0037-test.xlsx',
    '<temporary-chz-export>',
    'V0037-IMPORT-HASH',
    'temporary-chz-export',
    'a3700000-0000-0000-0000-000000000001',
    'Imported',
    1,
    1,
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z');
""");
    }

    private static async Task SeedPreflightScenarioAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, """
INSERT INTO item_types(name, code, enable_marking)
VALUES('V0037 marking type', 'V0037_MARKING_TYPE', TRUE);

INSERT INTO items(name, barcode, gtin, item_type_id)
SELECT 'V0037 marking item', 'V0037-MARKING-ITEM', '04600000009370', id
FROM item_types
WHERE code = 'V0037_MARKING_TYPE';

INSERT INTO orders(order_ref, order_type, status, created_at, marking_responsibility)
VALUES('V0037-ORDER', 'INTERNAL', 'ACCEPTED', '2026-08-24T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(order_id, item_id, qty_ordered)
SELECT orders.id, items.id, 6
FROM orders
CROSS JOIN items
WHERE orders.order_ref = 'V0037-ORDER'
  AND items.barcode = 'V0037-MARKING-ITEM';

INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin,
    required_quantity, reserve_quantity, requested_quantity,
    request_number, status, source_type, source_order_id,
    created_at, updated_at)
SELECT
    'a3710000-0000-0000-0000-000000000001',
    orders.id,
    order_lines.id,
    items.id,
    items.gtin,
    6,
    0,
    6,
    'V0037-PREFLIGHT-REQUEST',
    'Printed',
    'PRODUCTION_ORDER',
    orders.id,
    '2026-08-24T10:00:00.000Z',
    '2026-08-24T10:00:00.000Z'
FROM orders
INNER JOIN order_lines ON order_lines.order_id = orders.id
INNER JOIN items ON items.id = order_lines.item_id
WHERE orders.order_ref = 'V0037-ORDER';

INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, imported_rows, valid_code_rows,
    created_at, processed_at)
VALUES
('a3710000-0000-0000-0000-000000000101', 'exact.xlsx', '<temporary-chz-export>',
 'V0037-MATRIX-IMPORT-EXACT', 'temporary-chz-export',
 'a3710000-0000-0000-0000-000000000001', 'Imported', 4, 4,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000102', 'wrong-source.xlsx', '<temporary-chz-export>',
 'V0037-MATRIX-IMPORT-WRONG-SOURCE', 'csv',
 'a3710000-0000-0000-0000-000000000001', 'Imported', 1, 1,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000103', 'wrong-path.xlsx', 'wrong-path.xlsx',
 'V0037-MATRIX-IMPORT-WRONG-PATH', 'temporary-chz-export',
 'a3710000-0000-0000-0000-000000000001', 'Imported', 1, 1,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z');

INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, source_row_number, created_at, updated_at)
VALUES
('a3710000-0000-0000-0000-000000000201', 'TEMP-CHZ-V0037-APPLIED',
 'V0037-MATRIX-HASH-1', '04600000009370', 'a3710000-0000-0000-0000-000000000001',
 'a3710000-0000-0000-0000-000000000101', 'Applied', 'HistoricalUnknown', 1,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000202', 'TEMP-CHZ-V0037-RESERVED',
 'V0037-MATRIX-HASH-2', '04600000009370', 'a3710000-0000-0000-0000-000000000001',
 'a3710000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', 2,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000203', 'NOT-TEMP-V0037-MATRIX',
 'V0037-MATRIX-HASH-3', '04600000009370', 'a3710000-0000-0000-0000-000000000001',
 'a3710000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', 3,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000204', 'TEMP-CHZ-V0037-MATRIX-WRONG-SOURCE',
 'V0037-MATRIX-HASH-4', '04600000009370', 'a3710000-0000-0000-0000-000000000001',
 'a3710000-0000-0000-0000-000000000102', 'Reserved', 'HistoricalUnknown', 4,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000205', 'TEMP-CHZ-V0037-MATRIX-WRONG-PATH',
 'V0037-MATRIX-HASH-5', '04600000009370', 'a3710000-0000-0000-0000-000000000001',
 'a3710000-0000-0000-0000-000000000103', 'Reserved', 'HistoricalUnknown', 5,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z'),
('a3710000-0000-0000-0000-000000000206', 'TEMP-CHZ-V0037-MATRIX-QUARANTINED',
 'V0037-MATRIX-HASH-6', '04600000009370', 'a3710000-0000-0000-0000-000000000001',
 'a3710000-0000-0000-0000-000000000101', 'Quarantined', 'HistoricalUnknown', 6,
 '2026-08-24T10:00:00.000Z', '2026-08-24T10:00:00.000Z');
""");
    }

    private static async Task WithTemporaryDatabaseAsync(Func<NpgsqlConnection, Task> test)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
        }

        var databaseName = $"flowstock_marking_v0037_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        var testBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
            Pooling = false,
            PersistSecurityInfo = true,
            CommandTimeout = 120
        };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var connection = new NpgsqlConnection(testBuilder.ConnectionString);
            await connection.OpenAsync();
            await test(connection);
        }
        finally
        {
            if (!databaseName.StartsWith("flowstock_marking_v0037_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe temporary database name.");
            }

            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task ApplyMigrationsThroughAsync(NpgsqlConnection connection, int maxVersion)
    {
        await ApplyMigrationsAsync(connection, 1, maxVersion);
    }

    private static async Task ApplyMigrationsAsync(
        NpgsqlConnection connection,
        int minVersion,
        int maxVersion)
    {
        foreach (var migration in FindMigrations().Where(path =>
                     Version(path) >= minVersion && Version(path) <= maxVersion))
        {
            await ApplyMigrationAsync(connection, migration);
        }
    }

    private static async Task ApplyMigrationAsync(NpgsqlConnection connection, string path)
    {
        _ = await ExecuteMigrationAsync(connection, path);
    }

    private static async Task<int> ExecuteMigrationAsync(NpgsqlConnection connection, string path)
    {
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(path), connection)
        {
            CommandTimeout = 120
        };
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ExecuteScalarStringAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadOriginsByCodeAsync(
        NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT code, origin FROM marking_code ORDER BY code;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0), reader.GetString(1));
        }

        return result;
    }

    private static async Task<string> ReadMarkingCodesExceptOriginFingerprintAsync(
        NpgsqlConnection connection)
    {
        return await ExecuteScalarStringAsync(connection, """
SELECT MD5(COALESCE(STRING_AGG(payload, '|' ORDER BY payload), ''))
FROM (
    SELECT (TO_JSONB(marking_row) - 'origin')::text AS payload
    FROM marking_code AS marking_row
) AS rows;
""");
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadTableFingerprintsAsync(
        NpgsqlConnection connection,
        IEnumerable<string> tableNames)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tableName in tableNames)
        {
            var fingerprint = await ExecuteScalarStringAsync(connection, $"""
SELECT MD5(COALESCE(STRING_AGG(payload, '|' ORDER BY payload), ''))
FROM (
    SELECT TO_JSONB(row)::text AS payload
    FROM {tableName} AS row
) AS rows;
""");
            result.Add(tableName, fingerprint);
        }

        return result;
    }

    private static string FindMigration(int version) =>
        FindMigrations().Single(path => Version(path) == version);

    private static string[] FindMigrations() =>
        Directory.GetFiles(
                Path.Combine(FindRepoRoot(), "deploy", "postgres", "migrations"),
                "V*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

    private static int Version(string path) =>
        int.Parse(Path.GetFileName(path).AsSpan(1, 4), CultureInfo.InvariantCulture);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
