using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingAggregateUpgradeMigrationPostgresTests
{
    [Fact]
    public async Task V0036_UpgradeFromV0030_MapsHistoricalCorrectedPalletToSupersededSubject()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
        }

        var databaseName = $"flowstock_marking_upgrade_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        var upgradeBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
            Pooling = false,
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
            var migrations = Directory.GetFiles(
                    Path.Combine(FindRepoRoot(), "deploy", "postgres", "migrations"),
                    "V*.sql")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();

            await using var connection = new NpgsqlConnection(upgradeBuilder.ConnectionString);
            await connection.OpenAsync();
            foreach (var migration in migrations.TakeWhile(path => Version(path) <= 30))
            {
                await ApplyMigrationAsync(connection, migration);
            }

            var palletId = await SeedCorrectedPalletAsync(connection);

            foreach (var migration in migrations.Where(path => Version(path) > 30))
            {
                await ApplyMigrationAsync(connection, migration);
            }

            await using (var assertion = new NpgsqlCommand("""
SELECT subject.lifecycle, component.marking_subject_id IS NOT NULL
FROM production_pallets pallet
INNER JOIN production_pallet_lines component ON component.production_pallet_id = pallet.id
INNER JOIN marking_production_subject subject ON subject.id = component.marking_subject_id
WHERE pallet.id = @pallet_id;
""", connection))
            {
                assertion.Parameters.AddWithValue("@pallet_id", palletId);
                await using var reader = await assertion.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("SUPERSEDED", reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
            }

            var activeSubject = await SeedActiveSubjectAsync(connection, palletId);
            var parentAllowlistId = await SeedAllowlistParentAsync(connection, activeSubject);
            var store = new PostgresDataStore(upgradeBuilder.ConnectionString);
            var approvedSnapshot = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            var staleApprovalId = await SeedStaleApprovalAsync(
                connection,
                activeSubject,
                parentAllowlistId,
                $"stale-{approvedSnapshot.Hash}");
            var currentSnapshot = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            Assert.Equal(approvedSnapshot.Hash, currentSnapshot.Hash);

            Assert.DoesNotContain(currentSnapshot.Entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_", StringComparison.Ordinal)
                || entry.IssueCode == "MARKING_CUTOVER_STALE_SUBJECT_APPROVAL");

            var allowanceId = await SeedAllowanceAsync(connection, staleApprovalId, activeSubject);
            var coverageId = await AssertSingleGrandfatherConsumerUnderConcurrencyAsync(
                upgradeBuilder.ConnectionString,
                allowanceId,
                activeSubject.SubjectId);

            var replacement = await SeedActiveSubjectAsync(connection, palletId);
            await SeedReadyFactAsync(connection, activeSubject);
            await using (var enforceForCorrection = new NpgsqlCommand(
                             "UPDATE marking_cutover_state SET state = 'ENFORCED' WHERE id = TRUE",
                             connection))
            {
                Assert.Equal(1, await enforceForCorrection.ExecuteNonQueryAsync());
            }

            store.ExecuteInTransaction(scopedStore =>
                ((IMarkingAggregateStore)scopedStore).SupersedeReadyHuFactsForCorrection(
                    activeSubject.PrdDocId,
                    replacement.PalletId,
                    "upgrade-correction-test",
                    DateTime.UtcNow));

            await using var correctionAssertion = new NpgsqlCommand("""
SELECT COUNT(*) FILTER (WHERE coverage.retired_at IS NULL),
       (ARRAY_AGG(coverage.marking_subject_id) FILTER (WHERE coverage.retired_at IS NULL))[1],
       MAX(consumption.active_quantity) FILTER (WHERE coverage.retired_at IS NULL),
       COUNT(*) FILTER (WHERE coverage.id = @source_coverage_id AND coverage.retired_at IS NOT NULL),
       COUNT(*) FILTER (WHERE coverage.id = @source_coverage_id AND consumption.active_quantity = 0)
FROM marking_operational_coverage coverage
INNER JOIN marking_operational_coverage_consumption consumption
        ON consumption.operational_coverage_id = coverage.id
WHERE coverage.grandfather_allowance_id = @allowance_id;
""", connection);
            correctionAssertion.Parameters.AddWithValue("@source_coverage_id", coverageId);
            correctionAssertion.Parameters.AddWithValue("@allowance_id", allowanceId);
            await using var correctionReader = await correctionAssertion.ExecuteReaderAsync();
            Assert.True(await correctionReader.ReadAsync());
            Assert.Equal(1L, correctionReader.GetInt64(0));
            Assert.Equal(replacement.SubjectId, correctionReader.GetGuid(1));
            Assert.Equal(100m, correctionReader.GetDecimal(2));
            Assert.Equal(1L, correctionReader.GetInt64(3));
            Assert.Equal(1L, correctionReader.GetInt64(4));
        }
        finally
        {
            if (!databaseName.StartsWith("flowstock_marking_upgrade_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe temporary database name.");
            }

            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task ApplyMigrationAsync(NpgsqlConnection connection, string path)
    {
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(path), connection)
        {
            CommandTimeout = 120
        };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> SeedCorrectedPalletAsync(NpgsqlConnection connection)
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var command = new NpgsqlCommand("""
WITH item_type AS (
    INSERT INTO item_types(name, code, enable_marking)
    VALUES(@item_type_name, @item_type_code, TRUE)
    RETURNING id
), item AS (
    INSERT INTO items(name, barcode, gtin, base_uom, item_type_id)
    SELECT @item_name, @barcode, @gtin, 'шт', item_type.id FROM item_type
    RETURNING id
), source_order AS (
    INSERT INTO orders(order_ref, order_type, status, created_at, marking_status)
    VALUES(@order_ref, 'INTERNAL', 'DRAFT', @now, 'NOT_REQUIRED')
    RETURNING id
), source_line AS (
    INSERT INTO order_lines(order_id, item_id, qty_ordered)
    SELECT source_order.id, item.id, 100 FROM source_order CROSS JOIN item
    RETURNING id, order_id, item_id
), location AS (
    INSERT INTO locations(code, name) VALUES(@location_code, @location_name)
    RETURNING id
), production_doc AS (
    INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
    SELECT @doc_ref, 'PRODUCTION_RECEIPT', 'DRAFT', @now, source_order.id, @order_ref
    FROM source_order
    RETURNING id, order_id
), production_line AS (
    INSERT INTO doc_lines(doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
    SELECT production_doc.id, source_line.id, source_line.item_id, 100, location.id, @hu
    FROM production_doc CROSS JOIN source_line CROSS JOIN location
    RETURNING id, doc_id, order_line_id, item_id, to_location_id
), pallet AS (
    INSERT INTO production_pallets(
        prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
        hu_code, planned_qty, to_location_id, status, created_at)
    SELECT production_line.doc_id, production_line.id, source_line.order_id,
           production_line.order_line_id, production_line.item_id,
           @hu, 100, production_line.to_location_id, 'CORRECTED', @now
    FROM production_line CROSS JOIN source_line
    RETURNING id
), component AS (
    INSERT INTO production_pallet_lines(
        production_pallet_id, doc_line_id, order_line_id, item_id,
        planned_qty, filled_qty, created_at)
    SELECT pallet.id, production_line.id, production_line.order_line_id,
           production_line.item_id, 100, 100, @now
    FROM pallet CROSS JOIN production_line
)
SELECT id FROM pallet;
""", connection);
        command.Parameters.AddWithValue("@item_type_name", $"Upgrade marking {suffix}");
        command.Parameters.AddWithValue("@item_type_code", $"UPGRADE_{suffix}");
        command.Parameters.AddWithValue("@item_name", $"Upgrade item {suffix}");
        command.Parameters.AddWithValue("@barcode", $"UPGRADE-{suffix}");
        command.Parameters.AddWithValue("@gtin", "04600000000001");
        command.Parameters.AddWithValue("@order_ref", $"UPGRADE-{suffix}");
        command.Parameters.AddWithValue("@now", "2026-08-24T10:00:00Z");
        command.Parameters.AddWithValue("@location_code", $"UP-{suffix}");
        command.Parameters.AddWithValue("@location_name", $"Upgrade location {suffix}");
        command.Parameters.AddWithValue("@doc_ref", $"PRD-UPGRADE-{suffix}");
        command.Parameters.AddWithValue("@hu", $"HU-UPGRADE-{suffix}");
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<ActiveSubjectSnapshot> SeedActiveSubjectAsync(
        NpgsqlConnection connection,
        long correctedPalletId)
    {
        await using var command = new NpgsqlCommand("""
WITH source AS (
    SELECT pallet.prd_doc_id, pallet.order_id, component.order_line_id,
           component.item_id, pallet.to_location_id
    FROM production_pallets pallet
    INNER JOIN production_pallet_lines component ON component.production_pallet_id = pallet.id
    WHERE pallet.id = @pallet_id
), production_line AS (
    INSERT INTO doc_lines(doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
    SELECT source.prd_doc_id, source.order_line_id, source.item_id, 100,
           source.to_location_id, @hu
    FROM source
    RETURNING id, doc_id, order_line_id, item_id, to_location_id
), pallet AS (
    INSERT INTO production_pallets(
        prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
        hu_code, planned_qty, to_location_id, status, created_at)
    SELECT production_line.doc_id, production_line.id, source.order_id,
           production_line.order_line_id, production_line.item_id,
           @hu, 100, production_line.to_location_id, 'PLANNED', @now
    FROM production_line CROSS JOIN source
    RETURNING id
), component AS (
    INSERT INTO production_pallet_lines(
        production_pallet_id, doc_line_id, order_line_id, item_id,
        planned_qty, filled_qty, created_at)
    SELECT pallet.id, production_line.id, production_line.order_line_id,
           production_line.item_id, 100, 0, @now
    FROM pallet CROSS JOIN production_line
    RETURNING id, marking_subject_id, order_line_id, item_id
)
SELECT component.id, component.marking_subject_id, component.order_line_id,
       component.item_id, item.gtin, pallet.id, production_line.doc_id,
       production_line.id, @hu
FROM component
INNER JOIN items item ON item.id = component.item_id
CROSS JOIN pallet
CROSS JOIN production_line;
""", connection);
        command.Parameters.AddWithValue("@pallet_id", correctedPalletId);
        command.Parameters.AddWithValue("@hu", $"HU-ACTIVE-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@now", "2026-08-24T10:01:00Z");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ActiveSubjectSnapshot(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetString(8));
    }

    private static async Task<long> SeedAllowlistParentAsync(
        NpgsqlConnection connection,
        ActiveSubjectSnapshot subject)
    {
        await using var command = new NpgsqlCommand("""
INSERT INTO marking_synthetic_legacy_allowlist(
    order_line_id, allowed_synthetic_qty, target_qty_at_cutover,
    approved_at, approved_by, preflight_hash)
VALUES(@order_line_id, 100, 100, @now, 'upgrade-test', 'v0027-parent-snapshot')
RETURNING id;
""", connection);
        command.Parameters.AddWithValue("@order_line_id", subject.OrderLineId);
        command.Parameters.AddWithValue("@now", "2026-08-24T10:01:30Z");
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<Guid> SeedStaleApprovalAsync(
        NpgsqlConnection connection,
        ActiveSubjectSnapshot subject,
        long parentAllowlistId,
        string staleHash)
    {
        await using var command = new NpgsqlCommand("""
INSERT INTO marking_synthetic_legacy_allowlist_subject(
    id, allowlist_id, marking_subject_id, component_id_snapshot,
    item_id_snapshot, gtin_snapshot, subject_quantity_at_cutover,
    approved_quantity, preflight_hash, approved_at, approved_by,
    subject_revision_at_cutover)
SELECT @approval_id, @allowlist_id, subject.id, subject.current_component_id,
       subject.item_id, subject.gtin, subject.subject_quantity, 100,
       @hash, @now, 'upgrade-test', subject.revision
FROM marking_production_subject subject
WHERE subject.id = @subject_id;
""", connection);
        command.Parameters.AddWithValue("@now", "2026-08-24T10:02:00Z");
        command.Parameters.AddWithValue("@hash", staleHash);
        var approvalId = Guid.NewGuid();
        command.Parameters.AddWithValue("@approval_id", approvalId);
        command.Parameters.AddWithValue("@allowlist_id", parentAllowlistId);
        command.Parameters.AddWithValue("@subject_id", subject.SubjectId);
        await command.ExecuteNonQueryAsync();
        return approvalId;
    }

    private static async Task<Guid> SeedAllowanceAsync(
        NpgsqlConnection connection,
        Guid approvalId,
        ActiveSubjectSnapshot subject)
    {
        var allowanceId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
INSERT INTO marking_grandfather_operational_allowance(
    id, allowlist_subject_id, marking_subject_id, approved_quantity,
    cutover_subject_revision, preflight_hash, created_at, usable_quantity_cap)
SELECT @allowance_id, @approval_id, @subject_id, 100,
       revision, @hash, @now, 100
FROM marking_production_subject
WHERE id = @subject_id;
""", connection);
        command.Parameters.AddWithValue("@allowance_id", allowanceId);
        command.Parameters.AddWithValue("@approval_id", approvalId);
        command.Parameters.AddWithValue("@subject_id", subject.SubjectId);
        command.Parameters.AddWithValue("@hash", "stale-concurrency-test");
        command.Parameters.AddWithValue("@now", "2026-08-24T10:03:00Z");
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return allowanceId;
    }

    private static async Task<Guid> AssertSingleGrandfatherConsumerUnderConcurrencyAsync(
        string connectionString,
        Guid allowanceId,
        Guid subjectId)
    {
        var firstCoverageId = Guid.NewGuid();
        await using var firstConnection = new NpgsqlConnection(connectionString);
        await using var secondConnection = new NpgsqlConnection(connectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        await using var secondTransaction = await secondConnection.BeginTransactionAsync();

        await using (var first = new NpgsqlCommand("""
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, source_type, covered_quantity,
    grandfather_allowance_id, created_at)
VALUES(@id, @subject_id, 'GRANDFATHER_ALLOWANCE', 100, @allowance_id, @now);
""", firstConnection, firstTransaction))
        {
            first.Parameters.AddWithValue("@id", firstCoverageId);
            first.Parameters.AddWithValue("@subject_id", subjectId);
            first.Parameters.AddWithValue("@allowance_id", allowanceId);
            first.Parameters.AddWithValue("@now", "2026-08-24T10:04:00Z");
            Assert.Equal(1, await first.ExecuteNonQueryAsync());
        }

        var competingId = Guid.NewGuid();
        await using var competing = new NpgsqlCommand("""
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, source_type, covered_quantity,
    grandfather_allowance_id, created_at)
VALUES(@id, @subject_id, 'GRANDFATHER_ALLOWANCE', 100, @allowance_id, @now);
""", secondConnection, secondTransaction);
        competing.Parameters.AddWithValue("@id", competingId);
        competing.Parameters.AddWithValue("@subject_id", subjectId);
        competing.Parameters.AddWithValue("@allowance_id", allowanceId);
        competing.Parameters.AddWithValue("@now", "2026-08-24T10:04:01Z");
        var competingInsert = competing.ExecuteNonQueryAsync();
        await Task.Delay(150);
        Assert.False(competingInsert.IsCompleted);
        await firstTransaction.CommitAsync();
        var duplicate = await Assert.ThrowsAsync<PostgresException>(async () => await competingInsert);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        await secondTransaction.RollbackAsync();

        await using var idempotentConnection = new NpgsqlConnection(connectionString);
        await idempotentConnection.OpenAsync();
        await using var idempotent = new NpgsqlCommand("""
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, source_type, covered_quantity,
    grandfather_allowance_id, created_at)
VALUES(@id, @subject_id, 'GRANDFATHER_ALLOWANCE', 100, @allowance_id, @now)
ON CONFLICT (id) DO NOTHING;
""", idempotentConnection);
        idempotent.Parameters.AddWithValue("@id", firstCoverageId);
        idempotent.Parameters.AddWithValue("@subject_id", subjectId);
        idempotent.Parameters.AddWithValue("@allowance_id", allowanceId);
        idempotent.Parameters.AddWithValue("@now", "2026-08-24T10:04:00Z");
        Assert.Equal(0, await idempotent.ExecuteNonQueryAsync());
        return firstCoverageId;
    }

    private static async Task SeedReadyFactAsync(
        NpgsqlConnection connection,
        ActiveSubjectSnapshot subject)
    {
        await using var command = new NpgsqlCommand("""
WITH hu AS (
    INSERT INTO hus(hu_code, status, created_at)
    VALUES(@hu, 'ACTIVE', @now)
    ON CONFLICT (hu_code) DO UPDATE SET hu_code = EXCLUDED.hu_code
    RETURNING id
)
INSERT INTO marking_ready_hu_fact(
    id, marking_subject_id, receipt_doc_id, receipt_line_id,
    hu_id, hu_code_snapshot, item_id_snapshot, gtin_snapshot,
    marked_quantity, provenance, created_at)
SELECT @fact_id, @subject_id, @doc_id, @doc_line_id,
       hu.id, @hu, @item_id, @gtin, 100, 'GRANDFATHERED', @now
FROM hu;
""", connection);
        command.Parameters.AddWithValue("@fact_id", Guid.NewGuid());
        command.Parameters.AddWithValue("@subject_id", subject.SubjectId);
        command.Parameters.AddWithValue("@doc_id", subject.PrdDocId);
        command.Parameters.AddWithValue("@doc_line_id", subject.DocLineId);
        command.Parameters.AddWithValue("@hu", subject.Hu);
        command.Parameters.AddWithValue("@item_id", subject.ItemId);
        command.Parameters.AddWithValue("@gtin", subject.Gtin);
        command.Parameters.AddWithValue("@now", "2026-08-24T10:05:00Z");
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

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

    private sealed record ActiveSubjectSnapshot(
        long ComponentId,
        Guid SubjectId,
        long OrderLineId,
        long ItemId,
        string Gtin,
        long PalletId,
        long PrdDocId,
        long DocLineId,
        string Hu);
}
