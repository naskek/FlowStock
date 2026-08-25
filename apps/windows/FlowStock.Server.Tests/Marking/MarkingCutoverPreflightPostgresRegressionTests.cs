using FlowStock.Data;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingCutoverPreflightPostgresRegressionTests
{
    private const string DisposableDatabaseName = "flowstock_marking_cutover_test";

    [Fact]
    public void LegacyTaskRetirement_ReservedOnlyCandidate_IsAtomicAndIdempotentWithoutCodePrefixPredicate()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedLegacyTaskRetirementConflict(connection);
            var store = new PostgresDataStore(connection.ConnectionString);
            var before = new MarkingCutoverPreflightService(store).Run(
                new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc));
            Assert.Contains(before.Entries, entry =>
                entry.OrderLineId == 9501
                && entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT");

            var candidateId = Guid.Parse("95010000-0000-0000-0000-000000000001");
            var dryRun = store.DryRun(9501, candidateId, before.Hash, DateTime.UtcNow);

            Assert.True(dryRun.Eligible, string.Join(',', dryRun.BlockerCodes));
            Assert.Equal(2, dryRun.CandidateReservedQuantity);
            Assert.Equal(5, dryRun.RemainingAppliedQuantity);
            Assert.Equal(0, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
            Assert.Equal("Printed", ExecuteScalarString(connection,
                "SELECT status FROM marking_order WHERE id = '95010000-0000-0000-0000-000000000001';"));

            var codeSnapshot = ExecuteScalarString(connection, @"
SELECT md5(string_agg(id::text || ':' || code || ':' || code_hash || ':' || status || ':' || origin,
                      ',' ORDER BY id::text))
FROM marking_code
WHERE marking_order_id = '95010000-0000-0000-0000-000000000001';");
            var importSnapshot = ExecuteScalarString(connection, @"
SELECT md5(row_to_json(import_row)::text)
FROM marking_code_import import_row
WHERE id = '95010000-0000-0000-0000-000000000101';");

            var applied = store.Apply(
                9501,
                candidateId,
                before.Hash,
                dryRun.EligibilityHash,
                "retire-9501",
                "WPF:test",
                new DateTime(2026, 8, 25, 10, 1, 0, DateTimeKind.Utc));

            Assert.True(applied.WasApplied);
            Assert.False(applied.WasAlreadyApplied);
            Assert.Equal("SINGLE_TASK", applied.ResultingClassification);
            Assert.NotEqual(before.Hash, applied.PreflightHashAfter);
            Assert.Equal("Cancelled", ExecuteScalarString(connection,
                "SELECT status FROM marking_order WHERE id = '95010000-0000-0000-0000-000000000001';"));
            Assert.Equal(1, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
            Assert.Equal(codeSnapshot, ExecuteScalarString(connection, @"
SELECT md5(string_agg(id::text || ':' || code || ':' || code_hash || ':' || status || ':' || origin,
                      ',' ORDER BY id::text))
FROM marking_code
WHERE marking_order_id = '95010000-0000-0000-0000-000000000001';"));
            Assert.Equal(importSnapshot, ExecuteScalarString(connection, @"
SELECT md5(row_to_json(import_row)::text)
FROM marking_code_import import_row
WHERE id = '95010000-0000-0000-0000-000000000101';"));

            var replay = store.Apply(
                9501,
                candidateId,
                before.Hash,
                dryRun.EligibilityHash,
                "retire-9501",
                "WPF:test",
                DateTime.UtcNow);
            Assert.True(replay.WasAlreadyApplied);
            Assert.Equal(1, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
            Assert.Throws<InvalidOperationException>(() => store.Apply(
                9501, candidateId, before.Hash, "different-eligibility", "retire-9501",
                "WPF:test", DateTime.UtcNow));
            Assert.Throws<InvalidOperationException>(() => store.Apply(
                9501, candidateId, applied.PreflightHashAfter!, dryRun.EligibilityHash, "retire-9501-again",
                "WPF:test", DateTime.UtcNow));
            Assert.Equal(1, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
            Assert.DoesNotContain(new PostgresDataStore(connection.ConnectionString).GetMarkingCutoverPreflightEntries(), entry =>
                entry.OrderLineId == 9501 && entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT");

            Assert.Throws<InvalidOperationException>(() => store.MarkMarkingOrdersPrinted(
                [candidateId], DateTime.UtcNow));
            Execute(connection, @"
UPDATE marking_order SET status = 'Draft'
WHERE id = '95010000-0000-0000-0000-000000000002';");
            Assert.Throws<InvalidOperationException>(() => store.MarkMarkingOrdersPrinted(
                [candidateId, Guid.Parse("95010000-0000-0000-0000-000000000002")], DateTime.UtcNow));
            Assert.Equal("Draft", ExecuteScalarString(connection,
                "SELECT status FROM marking_order WHERE id = '95010000-0000-0000-0000-000000000002';"));
            Assert.Throws<PostgresException>(() => Execute(connection, @"
UPDATE marking_code
SET status = 'Applied', updated_at = '2026-08-25T10:02:00.000Z'
WHERE marking_order_id = '95010000-0000-0000-0000-000000000001';"));
            Assert.Throws<PostgresException>(() => Execute(connection, @"
UPDATE marking_code
SET marking_order_id = '95010000-0000-0000-0000-000000000002'
WHERE id = (SELECT id FROM marking_code
            WHERE marking_order_id = '95010000-0000-0000-0000-000000000001'
            ORDER BY id LIMIT 1);"));
            Assert.Throws<PostgresException>(() => Execute(connection, @"
UPDATE marking_code_import
SET matched_marking_order_id = '95010000-0000-0000-0000-000000000002'
WHERE id = '95010000-0000-0000-0000-000000000101';"));
            Assert.Throws<PostgresException>(() => Execute(connection, @"
INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id,
                         status, origin, created_at, updated_at)
VALUES ('95010000-0000-0000-0000-000000000999', 'V0039-NEW-RETIRED',
        'V0039-NEW-RETIRED-HASH', '04600000009501',
        '95010000-0000-0000-0000-000000000001',
        '95010000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic',
        '2026-08-25T10:02:00.000Z', '2026-08-25T10:02:00.000Z');"));
            Assert.Throws<PostgresException>(() => Execute(connection, @"
INSERT INTO marking_print_batch(id, marking_order_id, batch_number, status, created_at, updated_at)
VALUES ('95010000-0000-0000-0000-000000000901',
        '95010000-0000-0000-0000-000000000001', 1, 'Draft',
        '2026-08-25T10:02:00.000Z', '2026-08-25T10:02:00.000Z');"));
            Assert.Equal("Cancelled", ExecuteScalarString(connection,
                "SELECT status FROM marking_order WHERE id = '95010000-0000-0000-0000-000000000001';"));
        });
    }

    [Theory]
    [InlineData("UPDATE marking_code SET status = 'Applied' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code SET status = 'Voided' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code SET origin = 'RealImport' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code SET origin = 'LegacyRealImport' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code SET origin = 'HistoricalUnknown' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code SET origin = 'HistoricalUnknown', status = 'Quarantined' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code SET status = 'UnexpectedStatus' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000001' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_CANDIDATE_UNSAFE")]
    [InlineData("UPDATE marking_code_import SET source_type = 'csv' WHERE id = '95010000-0000-0000-0000-000000000101';", "MARKING_LEGACY_TASK_RETIREMENT_IMPORT_UNSAFE")]
    [InlineData("UPDATE marking_code_import SET storage_path = '<other>' WHERE id = '95010000-0000-0000-0000-000000000101';", "MARKING_LEGACY_TASK_RETIREMENT_IMPORT_UNSAFE")]
    [InlineData("UPDATE marking_code_import SET matched_marking_order_id = '95010000-0000-0000-0000-000000000002' WHERE id = '95010000-0000-0000-0000-000000000101';", "MARKING_LEGACY_TASK_RETIREMENT_IMPORT_UNSAFE")]
    [InlineData("UPDATE marking_code SET import_id = '95010000-0000-0000-0000-000000000101' WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000002' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_IMPORT_UNSAFE")]
    [InlineData("DELETE FROM marking_code WHERE id = (SELECT id FROM marking_code WHERE marking_order_id = '95010000-0000-0000-0000-000000000002' ORDER BY id LIMIT 1);", "MARKING_LEGACY_TASK_RETIREMENT_REMAINING_EVIDENCE_MISMATCH")]
    [InlineData("INSERT INTO marking_print_batch(id, marking_order_id, batch_number, status, created_at, updated_at) VALUES ('95010000-0000-0000-0000-000000000901', '95010000-0000-0000-0000-000000000001', 1, 'Draft', '2026-08-25T10:00:00.000Z', '2026-08-25T10:00:00.000Z');", "MARKING_LEGACY_TASK_RETIREMENT_LINEAGE_PRESENT")]
    public void LegacyTaskRetirement_UnsafeEvidenceOrLineage_RemainsFailClosed(
        string mutation,
        string expectedBlocker)
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedLegacyTaskRetirementConflict(connection);
            Execute(connection, mutation);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);

            var result = store.DryRun(
                9501,
                Guid.Parse("95010000-0000-0000-0000-000000000001"),
                preflight.Hash,
                DateTime.UtcNow);

            Assert.False(result.Eligible);
            Assert.Contains(expectedBlocker, result.BlockerCodes);
            Assert.Equal(0, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
            Assert.Equal("Printed", ExecuteScalarString(connection,
                "SELECT status FROM marking_order WHERE id = '95010000-0000-0000-0000-000000000001';"));
        });
    }

    [Fact]
    public void LegacyTaskRetirement_MultipleRemainingTasks_UsesAppliedOnlyAndBecomesAggregatable()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedLegacyTaskRetirementConflict(connection);
            Execute(connection, @"
UPDATE marking_code
SET status = 'Reserved'
WHERE id IN (
    SELECT id FROM marking_code
    WHERE marking_order_id = '95010000-0000-0000-0000-000000000002'
    ORDER BY id LIMIT 2);
INSERT INTO marking_order(id, order_id, item_id, gtin, requested_quantity, request_number,
                          status, source_type, source_order_id, request_status, created_at, updated_at)
VALUES ('95010000-0000-0000-0000-000000000003', 9501, 9501, '04600000009501', 2,
        'V0039-LEGITIMATE-2', 'Printed', 'PRODUCTION_ORDER', 9501, 'NotRequested',
        '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z');
INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type,
                                matched_marking_order_id, status, imported_rows, valid_code_rows,
                                duplicate_code_rows, created_at, processed_at)
VALUES ('95010000-0000-0000-0000-000000000103', 'synthetic-fixture.xlsx',
        '<temporary-chz-export>', 'V0039-REMAINING-IMPORT-2', 'temporary-chz-export',
        '95010000-0000-0000-0000-000000000003', 'Imported', 2, 2, 0,
        '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z');
INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id,
                         status, origin, created_at, updated_at)
SELECT ('95010000-0000-0000-0000-' || LPAD((400 + value)::text, 12, '0'))::uuid,
       'V0039-NONPREFIX-APPLIED-EXTRA-' || value, 'V0039-APPLIED-EXTRA-HASH-' || value,
       '04600000009501', '95010000-0000-0000-0000-000000000003',
       '95010000-0000-0000-0000-000000000103', 'Applied', 'LegacySynthetic',
       '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'
FROM generate_series(1, 2) value;");

            var store = new PostgresDataStore(connection.ConnectionString);
            var before = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            var candidateId = Guid.Parse("95010000-0000-0000-0000-000000000001");
            var dryRun = store.DryRun(9501, candidateId, before.Hash, DateTime.UtcNow);
            Assert.True(dryRun.Eligible, string.Join(',', dryRun.BlockerCodes));
            Assert.Equal(5, dryRun.RemainingAppliedQuantity);
            Assert.Equal(2, dryRun.RemainingReservedQuantity);

            var applied = store.Apply(
                9501, candidateId, before.Hash, dryRun.EligibilityHash,
                "retire-9501-multiple", "WPF:test", DateTime.UtcNow);

            Assert.Equal("MARKING_LEGACY_TASKS_AGGREGATABLE", applied.ResultingClassification);
            var after = new PostgresDataStore(connection.ConnectionString).GetMarkingCutoverPreflightEntries();
            Assert.Contains(after, entry =>
                entry.OrderLineId == 9501
                && entry.IssueCode == "MARKING_LEGACY_TASKS_AGGREGATABLE");
            Assert.Equal(5, applied.RemainingAppliedQuantity);
            Assert.Equal(2, applied.RemainingReservedQuantity);

            var postRetirement = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            var parent = store.ApproveMarkingCutoverLine(
                9501, null, postRetirement.Hash, "WPF:test", DateTime.UtcNow);
            Assert.Equal(5, parent.AllowedQuantity);
        });
    }

    [Fact]
    public void LegacyTaskRetirement_AndEnforce_SerializeOnCutoverStateWithoutStaleEnforcement()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedLegacyTaskRetirementConflict(connection);
            var store = new PostgresDataStore(connection.ConnectionString);
            var before = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            var candidateId = Guid.Parse("95010000-0000-0000-0000-000000000001");
            var dryRun = store.DryRun(9501, candidateId, before.Hash, DateTime.UtcNow);
            Assert.True(dryRun.Eligible, string.Join(',', dryRun.BlockerCodes));

            using var gateConnection = new NpgsqlConnection(connection.ConnectionString);
            gateConnection.Open();
            using var gateTransaction = gateConnection.BeginTransaction();
            using (var gate = gateConnection.CreateCommand())
            {
                gate.Transaction = gateTransaction;
                gate.CommandText = "SELECT state FROM marking_cutover_state WHERE id = TRUE FOR UPDATE;";
                _ = gate.ExecuteScalar();
            }

            var retirement = Task.Run(() => new PostgresDataStore(connection.ConnectionString).Apply(
                9501, candidateId, before.Hash, dryRun.EligibilityHash,
                "retire-9501-concurrent", "WPF:test", DateTime.UtcNow));
            Assert.True(SpinWait.SpinUntil(
                () => CountCutoverStateLockWaiters(connection.ConnectionString) >= 1,
                TimeSpan.FromSeconds(5)));

            var enforce = Task.Run(() =>
            {
                try
                {
                    new PostgresDataStore(connection.ConnectionString).EnforceMarkingCutover(
                        before.Hash, "WPF:test", DateTime.UtcNow);
                    return (string?)null;
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }
            });
            Assert.True(SpinWait.SpinUntil(
                () => CountCutoverStateLockWaiters(connection.ConnectionString) >= 2,
                TimeSpan.FromSeconds(5)));

            gateTransaction.Commit();
            Assert.True(Task.WaitAll([retirement, enforce], TimeSpan.FromSeconds(10)));

            Assert.True(retirement.Result.WasApplied);
            Assert.NotNull(enforce.Result);
            Assert.Equal("SHADOW", ExecuteScalarString(connection,
                "SELECT state FROM marking_cutover_state WHERE id = TRUE;"));
            Assert.Equal(1, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
        });
    }

    [Fact]
    public void LegacyTaskRetirement_ConcurrentCatalogDrift_FailsWithoutStatusOrAuditMutation()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedLegacyTaskRetirementConflict(connection);
            var store = new PostgresDataStore(connection.ConnectionString);
            var before = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            var candidateId = Guid.Parse("95010000-0000-0000-0000-000000000001");
            var dryRun = store.DryRun(9501, candidateId, before.Hash, DateTime.UtcNow);
            Assert.True(dryRun.Eligible, string.Join(',', dryRun.BlockerCodes));

            Execute(connection, @"
CREATE OR REPLACE FUNCTION test_pause_v0039_retirement_update()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.status = 'Printed' AND NEW.status = 'Cancelled' THEN
        PERFORM pg_advisory_xact_lock(95010039);
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER zz_test_pause_v0039_retirement_update
BEFORE UPDATE OF status ON marking_order
FOR EACH ROW
EXECUTE FUNCTION test_pause_v0039_retirement_update();");

            using var gateConnection = new NpgsqlConnection(connection.ConnectionString);
            gateConnection.Open();
            Execute(gateConnection, "SELECT pg_advisory_lock(95010039);");
            Task<MarkingLegacyTaskRetirementResult>? retirement = null;
            try
            {
                retirement = Task.Run(() => new PostgresDataStore(connection.ConnectionString).Apply(
                    9501, candidateId, before.Hash, dryRun.EligibilityHash,
                    "retire-9501-catalog-drift", "WPF:test", DateTime.UtcNow));
                Assert.True(SpinWait.SpinUntil(
                    () => CountAdvisoryLockWaiters(connection.ConnectionString) >= 1,
                    TimeSpan.FromSeconds(5)));

                using (var catalogConnection = new NpgsqlConnection(connection.ConnectionString))
                {
                    catalogConnection.Open();
                    using var catalogTransaction = catalogConnection.BeginTransaction(
                        System.Data.IsolationLevel.Serializable);
                    using var catalogMutation = catalogConnection.CreateCommand();
                    catalogMutation.Transaction = catalogTransaction;
                    catalogMutation.CommandText = @"
SET LOCAL session_replication_role = replica;
SELECT status
FROM marking_order
WHERE id = '95010000-0000-0000-0000-000000000001';
UPDATE items
SET gtin = '04600000009502'
WHERE id = 9501;";
                    catalogMutation.ExecuteNonQuery();
                    catalogTransaction.Commit();
                }

                Execute(gateConnection, "SELECT pg_advisory_unlock(95010039);");

                var error = Assert.ThrowsAny<Exception>(() => retirement.GetAwaiter().GetResult());
                var postgresError = Assert.IsType<PostgresException>(error);
                Assert.Equal(PostgresErrorCodes.SerializationFailure, postgresError.SqlState);
                Assert.Equal("Printed", ExecuteScalarString(connection,
                    "SELECT status FROM marking_order WHERE id = '95010000-0000-0000-0000-000000000001';"));
                Assert.Equal(0, ExecuteScalarInt(connection,
                    "SELECT COUNT(*) FROM marking_legacy_task_retirement_audit;"));
            }
            finally
            {
                Execute(gateConnection, "SELECT pg_advisory_unlock(95010039);");
                if (retirement is { IsCompleted: false })
                {
                    Assert.True(retirement.Wait(TimeSpan.FromSeconds(5)));
                }
                Execute(connection, @"
DROP TRIGGER IF EXISTS zz_test_pause_v0039_retirement_update ON marking_order;
DROP FUNCTION IF EXISTS test_pause_v0039_retirement_update();");
            }
        });
    }

    [Fact]
    public void LegacyTaskRetirement_RepresentativeFourShapes_KeepExactAppliedOnlyEvidence()
    {
        RunMutatingPostgresTest(connection =>
        {
            var shapes = new[]
            {
                new RepresentativeRetirementShape(454, Guid.Parse("bc65a644-5d31-4099-a00b-eb43e963aab2"), new[] { 5472 }, 0),
                new RepresentativeRetirementShape(468, Guid.Parse("c701ff12-e5da-457d-85dc-cc6795eaa619"), new[] { 1800, 600 }, 0),
                new RepresentativeRetirementShape(698, Guid.Parse("8e0e1f62-9e5c-4652-9b3b-04a24c45c918"), new[] { 1890 }, 0),
                new RepresentativeRetirementShape(699, Guid.Parse("e5ae33e8-7bb6-42f6-a142-c39334340e8e"), new[] { 1800 }, 2400)
            };
            for (var index = 0; index < shapes.Length; index++)
            {
                SeedRepresentativeRetirementShape(connection, shapes[index], index);
            }

            var store = new PostgresDataStore(connection.ConnectionString);
            foreach (var shape in shapes)
            {
                var current = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
                var dryRun = store.DryRun(shape.LineId, shape.CandidateId, current.Hash, DateTime.UtcNow);
                Assert.True(dryRun.Eligible, $"line={shape.LineId}; {string.Join(',', dryRun.BlockerCodes)}");
                Assert.Equal(shape.AppliedQuantities.Sum(), dryRun.RemainingAppliedQuantity);
                Assert.Equal(shape.RemainingReservedQuantity, dryRun.RemainingReservedQuantity);

                var applied = store.Apply(
                    shape.LineId,
                    shape.CandidateId,
                    current.Hash,
                    dryRun.EligibilityHash,
                    $"representative-{shape.LineId}",
                    "WPF:test",
                    DateTime.UtcNow);
                Assert.Equal(
                    shape.AppliedQuantities.Length > 1
                        ? "MARKING_LEGACY_TASKS_AGGREGATABLE"
                        : "SINGLE_TASK",
                    applied.ResultingClassification);
                Assert.DoesNotContain(store.GetMarkingCutoverPreflightEntries(), entry =>
                    entry.OrderLineId == shape.LineId
                    && entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT");
            }
        });
    }

    [Fact]
    public void PreflightReadModel_DoesNotMutateDatabase()
    {
        var connectionString = ResolveCutoverTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var before = ReadTableCounts(connectionString);

        var store = new PostgresDataStore(connectionString);
        _ = store.GetMarkingCutoverPreflightEntries();

        var after = ReadTableCounts(connectionString);

        Assert.Equal(before, after);
    }

    [Fact]
    public void PreflightReadModel_ScopesLineIssuesAndPrdPalletBlockersToOpenOrderLinkedRows()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9101, 'TEST-PR1 type scope', 'TEST-PR1-SCOPE', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9101, 'TEST-PR1 item scope', 'TEST-PR1-ITEM-SCOPE', '04600000009101', 9101);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES
(9101, 'TEST-PR1-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9102, 'TEST-PR1-SHIPPED', 'INTERNAL', 'SHIPPED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES
(9101, 9101, 9101, 1),
(9102, 9102, 9101, 1);

INSERT INTO marking_order(id, order_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('91010000-0000-0000-0000-000000000001', 9101, 9101, '04600000009101', 1, 'TEST-PR1-OPEN-TASK', 'Printed', 'PRODUCTION_ORDER', 9101, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91020000-0000-0000-0000-000000000001', 9102, 9101, '04600000009101', 1, 'TEST-PR1-SHIPPED-TASK', 'Printed', 'PRODUCTION_ORDER', 9102, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91030000-0000-0000-0000-000000000001', NULL, 9101, '04600000009101', 1, 'TEST-PR1-GLOBAL-TASK', 'Printed', 'PRODUCTION_NEED', NULL, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type, matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES
('91010000-0000-0000-0000-000000000101', 'test-open.xlsx', '<temporary-chz-export>', 'TEST-PR1-SCOPE-OPEN', 'temporary-chz-export', '91010000-0000-0000-0000-000000000001', 'Imported', 1, 1, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91020000-0000-0000-0000-000000000101', 'test-shipped.xlsx', '<temporary-chz-export>', 'TEST-PR1-SCOPE-SHIPPED', 'temporary-chz-export', '91020000-0000-0000-0000-000000000001', 'Imported', 1, 1, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91030000-0000-0000-0000-000000000101', 'test-global.xlsx', '<temporary-chz-export>', 'TEST-PR1-SCOPE-GLOBAL', 'temporary-chz-export', '91030000-0000-0000-0000-000000000001', 'Imported', 1, 1, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('91010000-0000-0000-0000-000000000201', 'TEST-PR1-SCOPE-OPEN-CODE', 'TEST-PR1-SCOPE-OPEN-HASH', '04600000009101', '91010000-0000-0000-0000-000000000001', '91010000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91020000-0000-0000-0000-000000000201', 'TEST-PR1-SCOPE-SHIPPED-CODE', 'TEST-PR1-SCOPE-SHIPPED-HASH', '04600000009101', '91020000-0000-0000-0000-000000000001', '91020000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91030000-0000-0000-0000-000000000201', 'TEST-PR1-SCOPE-GLOBAL-CODE', 'TEST-PR1-SCOPE-GLOBAL-HASH', '04600000009101', '91030000-0000-0000-0000-000000000001', '91030000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO docs(id, doc_ref, type, status, created_at, order_id, order_ref)
VALUES
(9101, 'TEST-PR1-OPEN-PRD', 'PRODUCTION_RECEIPT', 'DRAFT', '2026-06-26T10:00:00.000Z', 9101, 'TEST-PR1-OPEN'),
(9102, 'TEST-PR1-SHIPPED-PRD', 'PRODUCTION_RECEIPT', 'DRAFT', '2026-06-26T10:00:00.000Z', 9102, 'TEST-PR1-SHIPPED');

INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty)
VALUES
(9101, 9101, 9101, 9101, 1),
(9102, 9102, 9102, 9101, 1);

INSERT INTO production_pallets(id, prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code, planned_qty, status, created_at)
VALUES
(9101, 9101, 9101, 9101, 9101, 9101, 'TEST-PR1-OPEN-HU', 1, 'PLANNED', '2026-06-26T10:00:00.000Z'),
(9102, 9102, 9102, 9102, 9102, 9101, 'TEST-PR1-SHIPPED-HU', 1, 'PLANNED', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9101
                && entry.OrderLineId == 9101);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_TASK_LINE", StringComparison.Ordinal)
                && entry.OrderId == 9102);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_NOT_FOUND"
                && entry.Details.Contains("91030000-0000-0000-0000-000000000001", StringComparison.Ordinal));
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_OPEN_PRD"
                && entry.OrderId == 9101
                && entry.OrderLineId == 9101);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_OPEN_PRD"
                && entry.OrderId == 9102);
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.OrderId == 9101
                && entry.OrderLineId == 9101);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.OrderId == 9102);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_SYNTHETIC_PRESENT"
                && (entry.OrderId == 9102 || entry.OrderId == null));
        });
    }

    [Fact]
    public void CompletedFilledClosed_WithSufficientLedger_IsNotActiveProgressAndEnforceCreatesReadyHuFact()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "CLOSED",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: true,
                hasComponentFilledAt: true,
                ledgerQuantity: 100);

            var store = new PostgresDataStore(connection.ConnectionString);
            var entries = store.GetMarkingCutoverPreflightEntries();

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.IssueCode is "MARKING_ACTIVE_PALLET_PLAN" or "MARKING_FILLING_PROGRESS");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_SUBJECT_SNAPSHOT"
                && entry.Level == "info"
                && entry.Details.Contains("lifecycle=COMPLETED", StringComparison.Ordinal));

            var preflight = new MarkingCutoverPreflightService(store).Run(
                new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
            var repeatedPreflight = new MarkingCutoverPreflightService(store).Run(
                new DateTime(2026, 8, 24, 12, 0, 1, DateTimeKind.Utc));
            Assert.Equal(preflight.Hash, repeatedPreflight.Hash);
            Assert.Equal(preflight.CanonicalJson, repeatedPreflight.CanonicalJson);
            store.EnforceMarkingCutover(
                preflight.Hash,
                "cutover-history-test",
                new DateTime(2026, 8, 24, 12, 1, 0, DateTimeKind.Utc));

            Assert.Equal(1, ExecuteScalarInt(connection, @"
SELECT COUNT(*)
FROM marking_ready_hu_fact fact
INNER JOIN marking_production_subject subject ON subject.id = fact.marking_subject_id
WHERE subject.current_order_id = 9401
  AND fact.provenance = 'GRANDFATHERED'
  AND fact.reversed_at IS NULL;"));
        });
    }

    [Fact]
    public void CompletedFilledClosed_WithInsufficientLedger_IsNotActiveProgressAndDoesNotCreateReadyHuFact()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "CLOSED",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: true,
                hasComponentFilledAt: true,
                ledgerQuantity: 0);

            var store = new PostgresDataStore(connection.ConnectionString);
            var entries = store.GetMarkingCutoverPreflightEntries();

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.IssueCode is "MARKING_ACTIVE_PALLET_PLAN" or "MARKING_FILLING_PROGRESS");

            var preflight = new MarkingCutoverPreflightService(store).Run(
                new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT COUNT(*)
FROM (
    SELECT UPPER(BTRIM(COALESCE(hu_code, hu))) AS hu_code, item_id
    FROM ledger
    WHERE item_id = 9401
    GROUP BY UPPER(BTRIM(COALESCE(hu_code, hu))), item_id
    HAVING SUM(qty_delta) > 0.000001
) positive_balance;"));
            Assert.Equal(0, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_ready_hu_fact;"));
            store.EnforceMarkingCutover(
                preflight.Hash,
                "cutover-history-test",
                new DateTime(2026, 8, 24, 12, 1, 0, DateTimeKind.Utc));

            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT COUNT(*)
FROM marking_ready_hu_fact fact
INNER JOIN marking_production_subject subject ON subject.id = fact.marking_subject_id
WHERE subject.current_order_id = 9401
  AND fact.reversed_at IS NULL;"));
        });
    }

    [Fact]
    public void FilledPallet_WithOpenProductionDocument_RemainsFillingBlocker()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: true,
                hasComponentFilledAt: true,
                ledgerQuantity: 0);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_FILLING_PROGRESS"
                && entry.Level == "error");
        });
    }

    [Fact]
    public void FilledClosedPallet_WithoutMatchingCurrentSubject_RemainsFailClosed()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "CLOSED",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: true,
                hasComponentFilledAt: true,
                ledgerQuantity: 0);
            Execute(connection, @"
UPDATE marking_production_subject
SET current_doc_id = NULL
WHERE current_order_id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_FILLING_PROGRESS"
                && entry.Level == "error");
        });
    }

    [Theory]
    [InlineData("PRINTED", 0, true, false)]
    [InlineData("PLANNED", 25, false, true)]
    public void PlannedOrPrintedPallet_WithPartialFilling_RemainsFillingBlocker(
        string palletStatus,
        double filledQuantity,
        bool hasPalletFilledAt,
        bool hasComponentFilledAt)
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus,
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity,
                hasPalletFilledAt,
                hasComponentFilledAt,
                ledgerQuantity: 0);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_FILLING_PROGRESS"
                && entry.Level == "error");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Theory]
    [InlineData("PLANNED")]
    [InlineData("PRINTED")]
    public void PlannedOrPrintedPallet_WithMatchingActiveSubject_IsApprovableWarning(
        string palletStatus)
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus,
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "warning"
                && entry.Details == $"pallet_status={palletStatus}");
            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.IssueCode == "MARKING_FILLING_PROGRESS");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_SUBJECT_SNAPSHOT"
                && entry.Level == "error");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_OPEN_PRD"
                && entry.Level == "error");
        });
    }

    [Fact]
    public void PrintedPallet_WithoutSubject_RemainsFailClosed()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PRINTED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0,
                createMarkingSubject: false);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Equal(0, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_production_subject;"));
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Fact]
    public void PrintedPallet_WithMismatchedCurrentSubject_RemainsFailClosed()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PRINTED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            Execute(connection, @"
UPDATE marking_production_subject
SET current_doc_id = NULL
WHERE current_order_id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Theory]
    [InlineData("PLANNED")]
    [InlineData("PRINTED")]
    public void PlannedOrPrintedPallet_WithActiveSubjectOnClosedDocument_RemainsFailClosed(
        string palletStatus)
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus,
                documentStatus: "CLOSED",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Fact]
    public void PrintedPallet_WithMissingGtin_KeepsGtinAndActivePlanErrors()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PRINTED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0,
                gtin: null);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_GTIN_REQUIRED"
                && entry.Level == "error");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Theory]
    [InlineData("FILLED", "CLOSED", 100, true)]
    [InlineData("PRINTED", "DRAFT", 0, false)]
    public void ExplicitlyExemptComponent_DoesNotCreateMarkingCutoverBlockersOrSubject(
        string palletStatus,
        string documentStatus,
        double filledQuantity,
        bool hasFilledAt)
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus,
                documentStatus,
                plannedQuantity: 100,
                filledQuantity,
                hasPalletFilledAt: hasFilledAt,
                hasComponentFilledAt: hasFilledAt,
                ledgerQuantity: hasFilledAt ? 100 : 0,
                createMarkingSubject: false,
                gtin: null,
                chzMarkingExempt: true);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Equal(0, ExecuteScalarInt(connection, "SELECT COUNT(*) FROM marking_production_subject;"));
            Assert.DoesNotContain(entries, entry => entry.OrderLineId == 9401);
        });
    }

    [Fact]
    public void PreflightReadModel_AggregatesUnknownAndConflictEntriesAndExcludesQuarantinedCoverage()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9301, 'TEST-PR1 type aggregate', 'TEST-PR1-AGG', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9301, 'TEST-PR1 item aggregate', 'TEST-PR1-ITEM-AGG', '04600000009301', 9301);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9301, 'TEST-PR1-AGG-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9301, 9301, 9301, 1.5);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('93010000-0000-0000-0000-000000000001', 9301, 9301, 9301, '04600000009301', 1, 'TEST-PR1-AGG-SCOPED', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93020000-0000-0000-0000-000000000001', 9301, NULL, 9301, '04600000009301', 1, 'TEST-PR1-AGG-CONFLICT-A', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93030000-0000-0000-0000-000000000001', 9301, NULL, 9301, '04600000009301', 1, 'TEST-PR1-AGG-CONFLICT-B', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000001', 9301, NULL, 9301, '04600000009301', 1, 'TEST-PR1-AGG-UNKNOWN', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type, matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES
('93010000-0000-0000-0000-000000000101', 'agg-real.csv', '<memory>', 'TEST-PR1-AGG-REAL', 'csv', '93010000-0000-0000-0000-000000000001', 'Imported', 5, 5, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000101', 'agg-unknown.bin', '<memory>', 'TEST-PR1-AGG-UNKNOWN', 'unknown', '93040000-0000-0000-0000-000000000001', 'Imported', 2, 2, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('93010000-0000-0000-0000-000000000201', 'TEST-PR1-AGG-REAL-RESERVED', 'TEST-PR1-AGG-REAL-RESERVED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Reserved', 'RealImport', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000202', 'TEST-PR1-AGG-REAL-QUARANTINED', 'TEST-PR1-AGG-REAL-QUARANTINED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Quarantined', 'RealImport', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000203', 'TEST-PR1-AGG-REAL-VOIDED', 'TEST-PR1-AGG-REAL-VOIDED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Voided', 'RealImport', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000204', 'TEST-PR1-AGG-SYN-RESERVED', 'TEST-PR1-AGG-SYN-RESERVED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000205', 'TEST-PR1-AGG-SYN-VOIDED', 'TEST-PR1-AGG-SYN-VOIDED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Voided', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000201', 'TEST-PR1-AGG-UNKNOWN-1', 'TEST-PR1-AGG-UNKNOWN-1', '04600000009301', '93040000-0000-0000-0000-000000000001', '93040000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000202', 'TEST-PR1-AGG-UNKNOWN-2', 'TEST-PR1-AGG-UNKNOWN-2', '04600000009301', '93040000-0000-0000-0000-000000000001', '93040000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);
            var qtyIssue = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_QTY_NOT_INTEGER"
                && entry.OrderId == 9301
                && entry.OrderLineId == 9301));
            Assert.Equal(1, qtyIssue.RealCodeQty);
            Assert.Equal(0, qtyIssue.LegacySyntheticQty);

            var unknownIssue = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.Contains("93040000-0000-0000-0000-000000000001", StringComparison.Ordinal)));
            Assert.Contains("count=2", unknownIssue.Details);

            var conflictIssue = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT"
                && entry.OrderId == 9301
                && entry.OrderLineId == 9301));
            // The scoped task already bound to the line (93010000-...-001) must be part of the
            // conflict claims alongside the unscoped candidates.
            Assert.Contains("93010000-0000-0000-0000-000000000001", conflictIssue.Details, StringComparison.Ordinal);
            Assert.Equal(entries.Count, entries.Distinct().Count());
        });
    }

    [Fact]
    public void PreflightReadModel_ReportsConflictWhenScopedTaskAndUnscopedCandidateShareLine()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9350, 'TEST-PR1 type scoped conflict', 'TEST-PR1-SCOPEDCONF', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9350, 'TEST-PR1 item scoped conflict', 'TEST-PR1-ITEM-SCOPEDCONF', '04600000009350', 9350);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9350, 'TEST-PR1-SCOPEDCONF-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9350, 9350, 9350, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('93500000-0000-0000-0000-000000000001', 9350, 9350, 9350, '04600000009350', 1, 'TEST-PR1-SCOPEDCONF-SCOPED', 'Printed', 'PRODUCTION_ORDER', 9350, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93510000-0000-0000-0000-000000000001', 9350, NULL, 9350, '04600000009350', 1, 'TEST-PR1-SCOPEDCONF-UNSCOPED', 'Printed', 'PRODUCTION_ORDER', 9350, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // A line already bound to a scoped active task plus a second unscoped task whose only
            // candidate is that same (already occupied) line is a real SHADOW conflict that the
            // ux_marking_order_active_order_line unique index would otherwise block at enforcement.
            var conflict = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT"
                && entry.OrderId == 9350
                && entry.OrderLineId == 9350));
            Assert.Contains("93500000-0000-0000-0000-000000000001", conflict.Details, StringComparison.Ordinal);
            Assert.Contains("93510000-0000-0000-0000-000000000001", conflict.Details, StringComparison.Ordinal);

            // The conflict is the stronger diagnosis: the unscoped task must not also be reported as
            // merely UNASSIGNED.
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.Details.Contains("93510000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            Assert.Equal(entries.Count, entries.Distinct().Count());
        });
    }

    [Fact]
    public void ApprovalLifecycle_ParentChangesHash_ChildKeepsHash_AndEvidenceCannotBeConsumedTwice()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, enable_marking)
VALUES (9360, 'TEST-V0038-APPROVAL-TYPE', 'TEST-V0038-APPROVAL-TYPE', TRUE);
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9360, 'TEST-V0038-APPROVAL-ITEM', 'TEST-V0038-APPROVAL-ITEM', '04600000009360', 9360);
INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9360, 'TEST-V0038-APPROVAL-ORDER', 'INTERNAL', 'ACCEPTED', '2026-08-24T00:00:00.000Z', 'FLOWSTOCK');
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9360, 9360, 9360, 2);
INSERT INTO marking_production_subject(
    id, lifecycle, current_order_id, current_order_line_id, item_id, gtin,
    subject_quantity, created_at)
VALUES ('93600000-0000-0000-0000-000000000001', 'ACTIVE', 9360, 9360, 9360,
        '04600000009360', 2, '2026-08-24T00:00:00.000Z');
INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin, requested_quantity,
    request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES ('93600000-0000-0000-0000-000000000010', 9360, 9360, 9360,
        '04600000009360', 2, 'TEST-V0038-APPROVAL', 'Printed',
        'PRODUCTION_ORDER', 9360, '2026-08-24T00:00:00.000Z', '2026-08-24T00:00:00.000Z');
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES ('93600000-0000-0000-0000-000000000020', 'synthetic.xlsx',
        '<temporary-chz-export>', 'TEST-V0038-APPROVAL-IMPORT', 'temporary-chz-export',
        '93600000-0000-0000-0000-000000000010', 'Imported', 2, 2,
        '2026-08-24T00:00:00.000Z', '2026-08-24T00:00:00.000Z');
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('93600000-0000-0000-0000-000000000031', 'TEST-V0038-APPLIED-1', 'TEST-V0038-APPLIED-HASH-1',
 '04600000009360', '93600000-0000-0000-0000-000000000010', '93600000-0000-0000-0000-000000000020',
 'Applied', 'LegacySynthetic', '2026-08-24T00:00:00.000Z', '2026-08-24T00:00:00.000Z'),
('93600000-0000-0000-0000-000000000032', 'TEST-V0038-APPLIED-2', 'TEST-V0038-APPLIED-HASH-2',
 '04600000009360', '93600000-0000-0000-0000-000000000010', '93600000-0000-0000-0000-000000000020',
 'Applied', 'LegacySynthetic', '2026-08-24T00:00:00.000Z', '2026-08-24T00:00:00.000Z');
");

            var store = new PostgresDataStore(connection.ConnectionString);
            var service = new MarkingCutoverPreflightService(store);
            var h1 = service.Run(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc)).Hash;
            var parent = store.ApproveMarkingCutoverLine(
                9360, null, h1, "test-actor", new DateTime(2026, 8, 24, 12, 1, 0, DateTimeKind.Utc));

            Assert.NotEqual(h1, parent.CurrentPreflightHash);
            var h2 = parent.CurrentPreflightHash;
            var retry = store.ApproveMarkingCutoverLine(
                9360, null, h1, "test-actor", new DateTime(2026, 8, 24, 12, 1, 1, DateTimeKind.Utc));
            Assert.True(retry.WasAlreadyApproved);
            Assert.Equal(parent.AllowlistId, retry.AllowlistId);
            var duplicate = Assert.Throws<InvalidOperationException>(() =>
                store.ApproveMarkingCutoverLine(
                    9360, null, h2, "test-actor", new DateTime(2026, 8, 24, 12, 1, 2, DateTimeKind.Utc)));
            Assert.Contains("MARKING_LEGACY_LINE_ALREADY_APPROVED", duplicate.Message, StringComparison.Ordinal);
            Assert.Equal(1, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_synthetic_legacy_allowlist WHERE order_line_id = 9360;"));

            var child = store.ApproveMarkingCutoverSubjects(
                parent.AllowlistId,
                [new MarkingCutoverSubjectApprovalIntent(
                    Guid.Parse("93600000-0000-0000-0000-000000000001"), 2)],
                h2,
                "test-actor",
                new DateTime(2026, 8, 24, 12, 2, 0, DateTimeKind.Utc));
            Assert.Equal(h2, child.CurrentPreflightHash);
            Assert.Equal(h2, service.Run(new DateTime(2026, 8, 24, 12, 2, 1, DateTimeKind.Utc)).Hash);

            store.EnforceMarkingCutover(
                h2, "test-actor", new DateTime(2026, 8, 24, 12, 3, 0, DateTimeKind.Utc));
            Assert.Equal(1, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_grandfather_operational_allowance;"));
            Assert.Equal(1, ExecuteScalarInt(connection, @"
SELECT COUNT(*) FROM marking_operational_coverage
WHERE source_type = 'GRANDFATHER_ALLOWANCE' AND retired_at IS NULL;"));
        });
    }

    [Fact]
    public void PreflightReadModel_ExcludesNonMarkableLinesFromLineScope()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9150, 'TEST-PR1 type non-markable', 'TEST-PR1-NONMARK', 1, TRUE, TRUE, FALSE, FALSE, FALSE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9150, 'TEST-PR1 item non-markable', 'TEST-PR1-ITEM-NONMARK', '04600000009150', 9150);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9150, 'TEST-PR1-NONMARK-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9150, 9150, 9150, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES ('91500000-0000-0000-0000-000000000001', 9150, NULL, 9150, '04600000009150', 1, 'TEST-PR1-NONMARK-TASK', 'Printed', 'PRODUCTION_ORDER', 9150, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // The non-markable line is never used as a line mapping candidate and never receives a
            // line-level marking issue.
            Assert.DoesNotContain(entries, entry => entry.OrderLineId == 9150);

            // The unscoped legacy task still surfaces as having no markable candidate line, proving
            // the non-markable line was not silently bound to it.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_NOT_FOUND"
                && entry.OrderId == 9150
                && entry.OrderLineId == null
                && entry.Details.Contains("91500000-0000-0000-0000-000000000001", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void PreflightReadModel_TreatsOrderAndSourceOrderAsTwoExplicitLinks()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9200, 'TEST-PR1 type links', 'TEST-PR1-LINKS', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9200, 'TEST-PR1 item links', 'TEST-PR1-ITEM-LINKS', '04600000009200', 9200);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES
(9201, 'TEST-PR1-LINK-O1', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9202, 'TEST-PR1-LINK-O2', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9203, 'TEST-PR1-LINK-TERMINAL', 'INTERNAL', 'SHIPPED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9204, 'TEST-PR1-LINK-O4', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9205, 'TEST-PR1-LINK-O5', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES
(9201, 9201, 9200, 1),
(9202, 9202, 9200, 1),
(9204, 9204, 9200, 1),
(9205, 9205, 9200, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('92010000-0000-0000-0000-000000000001', 9201, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-ORDERONLY', 'Printed', 'PRODUCTION_ORDER', NULL, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92020000-0000-0000-0000-000000000001', NULL, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-SOURCEONLY', 'Printed', 'PRODUCTION_ORDER', 9202, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92030000-0000-0000-0000-000000000001', 9203, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-TERMOPEN', 'Printed', 'PRODUCTION_ORDER', 9204, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92050000-0000-0000-0000-000000000001', 9205, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-BOTHSAME', 'Printed', 'PRODUCTION_ORDER', 9205, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92060000-0000-0000-0000-000000000001', 9201, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-BOTHDIFF', 'Printed', 'PRODUCTION_ORDER', 9202, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // order_id only -> open via order_id.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9201
                && entry.OrderLineId == 9201
                && entry.Details.Contains("92010000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // source_order_id only -> open via source_order_id.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9202
                && entry.OrderLineId == 9202
                && entry.Details.Contains("92020000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // both links set to the same id -> open.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9205
                && entry.OrderLineId == 9205
                && entry.Details.Contains("92050000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // order_id terminal, source_order_id open, both set and different -> conflict, no silent
            // mapping to the open source order.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT"
                && entry.Details.Contains("92030000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("order_id=9203", StringComparison.Ordinal)
                && entry.Details.Contains("source_order_id=9204", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_TASK_LINE", StringComparison.Ordinal)
                && entry.Details.Contains("92030000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // both links open but different -> conflict, no arbitrary mapping.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT"
                && entry.Details.Contains("92060000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("order_id=9201", StringComparison.Ordinal)
                && entry.Details.Contains("source_order_id=9202", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_TASK_LINE", StringComparison.Ordinal)
                && entry.Details.Contains("92060000-0000-0000-0000-000000000001", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void PreflightReadModel_ReportsHistoricalUnknownEvenForCancelledOrFailedTasks()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9180, 'TEST-PR1 type hu', 'TEST-PR1-HU', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9180, 'TEST-PR1 item hu', 'TEST-PR1-ITEM-HU', '04600000009180', 9180);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9180, 'TEST-PR1-HU-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9180, 9180, 9180, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('91800000-0000-0000-0000-000000000001', 9180, NULL, 9180, '04600000009180', 1, 'TEST-PR1-HU-CANCELLED', 'Cancelled', 'PRODUCTION_ORDER', 9180, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91810000-0000-0000-0000-000000000001', 9180, NULL, 9180, '04600000009180', 1, 'TEST-PR1-HU-FAILED', 'Failed', 'PRODUCTION_ORDER', 9180, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type, matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES ('91800000-0000-0000-0000-000000000101', 'hu.bin', '<memory>', 'TEST-PR1-HU-FILE', 'unknown', '91800000-0000-0000-0000-000000000001', 'Imported', 3, 3, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('91800000-0000-0000-0000-000000000201', 'TEST-PR1-HU-CANCELLED-1', 'TEST-PR1-HU-CANCELLED-1', '04600000009180', '91800000-0000-0000-0000-000000000001', '91800000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91800000-0000-0000-0000-000000000202', 'TEST-PR1-HU-CANCELLED-2', 'TEST-PR1-HU-CANCELLED-2', '04600000009180', '91800000-0000-0000-0000-000000000001', '91800000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91810000-0000-0000-0000-000000000201', 'TEST-PR1-HU-FAILED-1', 'TEST-PR1-HU-FAILED-1', '04600000009180', '91810000-0000-0000-0000-000000000001', '91800000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // HistoricalUnknown classification is global: it must fire even though both owning
            // tasks are Cancelled / Failed (i.e. excluded from active_tasks).
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.Contains("91800000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("count=2", StringComparison.Ordinal));
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.Contains("91810000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("count=1", StringComparison.Ordinal));
        });
    }

    private static IReadOnlyDictionary<string, long> ReadTableCounts(string connectionString)
    {
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            PersistSecurityInfo = true
        };
        using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT 'orders', COUNT(*) FROM orders
UNION ALL
SELECT 'order_lines', COUNT(*) FROM order_lines
UNION ALL
SELECT 'marking_order', COUNT(*) FROM marking_order
UNION ALL
SELECT 'marking_code', COUNT(*) FROM marking_code
UNION ALL
SELECT 'marking_cutover_state', COUNT(*) FROM marking_cutover_state;";

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    private static IReadOnlyList<MarkingCutoverPreflightEntry> ReadPreflightEntries(string connectionString)
    {
        return new PostgresDataStore(connectionString).GetMarkingCutoverPreflightEntries();
    }

    private static void RunMutatingPostgresTest(Action<NpgsqlConnection> work)
    {
        // Mutation requires ALL THREE guards at once: the dedicated cutover test connection,
        // the explicit opt-in flag, and a database name that is provably disposable. A missing
        // connection or flag skips the test; an unsafe database name fails loudly.
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveCutoverTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            PersistSecurityInfo = true
        };
        using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        CleanupTestRows(connection);

        try
        {
            work(connection);
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    private static bool MutatingPostgresTestsEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("FLOWSTOCK_RUN_MUTATING_POSTGRES_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static void EnsureDisposableDatabase(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database();";
        var databaseName = command.ExecuteScalar() as string ?? string.Empty;

        var isDisposable =
            string.Equals(databaseName, DisposableDatabaseName, StringComparison.Ordinal)
            || databaseName.StartsWith(DisposableDatabaseName + "_", StringComparison.Ordinal);

        if (!isDisposable)
        {
            throw new InvalidOperationException(
                $"Refusing to run a mutating marking cutover test against database '{databaseName}'. " +
                $"Point FLOWSTOCK_MARKING_CUTOVER_TEST_CONNECTION at a disposable database named " +
                $"'{DisposableDatabaseName}' or prefixed with '{DisposableDatabaseName}_'.");
        }
    }

    private static void CleanupTestRows(NpgsqlConnection connection)
    {
        Execute(connection, @"
TRUNCATE TABLE ledger, production_pallet_lines, production_pallets, doc_lines, docs,
               orders, items, item_types, locations, hus,
               marking_order, marking_code_import, marking_code
CASCADE;

UPDATE marking_cutover_state
SET state = 'SHADOW',
    preflight_hash = NULL,
    preflight_generated_at = NULL,
    preflight_approved_at = NULL,
    preflight_approved_by = NULL,
    enforced_at = NULL,
    enforced_by = NULL,
    updated_at = '2026-08-24T00:00:00.000Z'
WHERE id = TRUE;");
    }

    private static void SeedMarkingPallet(
        NpgsqlConnection connection,
        string palletStatus,
        string documentStatus,
        double plannedQuantity,
        double filledQuantity,
        bool hasPalletFilledAt,
        bool hasComponentFilledAt,
        double ledgerQuantity,
        bool createMarkingSubject = true,
        string? gtin = "04600000009401",
        bool chzMarkingExempt = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO item_types(
    id, name, code, sort_order, is_active, is_visible_in_product_catalog,
    enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9401, 'TEST-CUTOVER-HISTORY-TYPE', 'TEST-CUTOVER-HISTORY-TYPE', 1,
        TRUE, TRUE, FALSE, FALSE, @enable_marking_during_insert);

INSERT INTO items(id, name, barcode, gtin, item_type_id, chz_marking_exempt)
VALUES (9401, 'TEST-CUTOVER-HISTORY-ITEM', 'TEST-CUTOVER-HISTORY-ITEM', @gtin, 9401, @chz_marking_exempt);

INSERT INTO locations(id, code, name)
VALUES (9401, 'TEST-CUTOVER-HISTORY-LOC', 'TEST-CUTOVER-HISTORY-LOC');

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9401, 'TEST-CUTOVER-HISTORY-ORDER', 'INTERNAL', 'ACCEPTED', @created_at, 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9401, 9401, 9401, @planned_quantity);

INSERT INTO docs(id, doc_ref, type, status, created_at, closed_at, order_id, order_ref)
VALUES (9401, 'TEST-CUTOVER-HISTORY-PRD', 'PRODUCTION_RECEIPT', @document_status,
        @created_at,
        CASE WHEN @document_status = 'CLOSED' THEN @closed_at ELSE NULL END,
        9401, 'TEST-CUTOVER-HISTORY-ORDER');

INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (9401, 9401, 9401, 9401, @planned_quantity, 9401, 'TEST-CUTOVER-HISTORY-HU');

INSERT INTO hus(hu_code, status, created_at)
VALUES ('TEST-CUTOVER-HISTORY-HU', 'ACTIVE', @created_at)
ON CONFLICT (hu_code) DO NOTHING;

INSERT INTO production_pallets(
    id, prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
    hu_code, planned_qty, to_location_id, status, filled_at, created_at)
VALUES (9401, 9401, 9401, 9401, 9401, 9401,
        'TEST-CUTOVER-HISTORY-HU', @planned_quantity, 9401, @pallet_status,
        CASE WHEN @has_pallet_filled_at THEN @filled_at ELSE NULL END,
        @created_at);

INSERT INTO production_pallet_lines(
    id, production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, filled_at, created_at)
VALUES (9401, 9401, 9401, 9401, 9401,
        @planned_quantity, @filled_quantity,
        CASE WHEN @has_component_filled_at THEN @filled_at ELSE NULL END,
        @created_at);

INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code)
VALUES (@closed_at, 9401, 9401, 9401, @ledger_quantity, 'TEST-CUTOVER-HISTORY-HU');
";
        command.Parameters.AddWithValue("@pallet_status", palletStatus);
        command.Parameters.AddWithValue("@document_status", documentStatus);
        command.Parameters.AddWithValue("@planned_quantity", plannedQuantity);
        command.Parameters.AddWithValue("@filled_quantity", filledQuantity);
        command.Parameters.AddWithValue("@has_pallet_filled_at", hasPalletFilledAt);
        command.Parameters.AddWithValue("@has_component_filled_at", hasComponentFilledAt);
        command.Parameters.AddWithValue("@ledger_quantity", ledgerQuantity);
        command.Parameters.AddWithValue(
            "@enable_marking_during_insert",
            createMarkingSubject && gtin != null && !chzMarkingExempt);
        command.Parameters.AddWithValue("@chz_marking_exempt", chzMarkingExempt);
        command.Parameters.Add(new NpgsqlParameter("@gtin", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = gtin ?? (object)DBNull.Value
        });
        command.Parameters.AddWithValue("@created_at", "2026-08-24T10:00:00.000Z");
        command.Parameters.AddWithValue("@filled_at", "2026-08-24T10:30:00.000Z");
        command.Parameters.AddWithValue("@closed_at", "2026-08-24T11:00:00.000Z");
        command.ExecuteNonQuery();

        if (gtin == null && !chzMarkingExempt)
        {
            Execute(connection, @"
ALTER TABLE item_types DISABLE TRIGGER trg_item_types_marking_applicability_transition;
UPDATE item_types SET enable_marking = TRUE WHERE id = 9401;
ALTER TABLE item_types ENABLE TRIGGER trg_item_types_marking_applicability_transition;");
        }
        else
        {
            Execute(connection, "UPDATE item_types SET enable_marking = TRUE WHERE id = 9401;");
        }
    }

    private static int ExecuteScalarInt(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ExecuteScalarString(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static int CountCutoverStateLockWaiters(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM pg_stat_activity
WHERE datname = current_database()
  AND wait_event_type = 'Lock'
  AND query LIKE '%marking_cutover_state%';";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountAdvisoryLockWaiters(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM pg_stat_activity
WHERE datname = current_database()
  AND wait_event_type = 'Lock'
  AND wait_event = 'advisory'
  AND query LIKE '%UPDATE marking_order%';";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SeedLegacyTaskRetirementConflict(NpgsqlConnection connection)
    {
        Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog,
                       enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9501, 'V0039 type', 'V0039-TYPE', 1, TRUE, TRUE, FALSE, FALSE, TRUE);
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9501, 'V0039 item', 'V0039-ITEM', '04600000009501', 9501);
INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9501, 'V0039-ORDER', 'INTERNAL', 'ACCEPTED', '2026-08-25T09:00:00.000Z', 'FLOWSTOCK');
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9501, 9501, 9501, 5);

INSERT INTO marking_order(id, order_id, item_id, gtin, requested_quantity, request_number,
                          status, source_type, source_order_id, request_status, created_at, updated_at)
VALUES
('95010000-0000-0000-0000-000000000001', 9501, 9501, '04600000009501', 2,
 'V0039-REDUNDANT', 'Printed', 'PRODUCTION_ORDER', 9501, 'NotRequested',
 '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'),
('95010000-0000-0000-0000-000000000002', 9501, 9501, '04600000009501', 5,
 'V0039-LEGITIMATE', 'Printed', 'PRODUCTION_ORDER', 9501, 'NotRequested',
 '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type,
                                matched_marking_order_id, status, imported_rows, valid_code_rows,
                                duplicate_code_rows, created_at, processed_at)
VALUES
('95010000-0000-0000-0000-000000000101', 'synthetic-fixture.xlsx', '<temporary-chz-export>',
 'V0039-CANDIDATE-IMPORT', 'temporary-chz-export', '95010000-0000-0000-0000-000000000001',
 'Imported', 2, 2, 0, '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'),
('95010000-0000-0000-0000-000000000102', 'synthetic-fixture.xlsx', '<temporary-chz-export>',
 'V0039-REMAINING-IMPORT', 'temporary-chz-export', '95010000-0000-0000-0000-000000000002',
 'Imported', 5, 5, 0, '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id,
                         status, origin, created_at, updated_at)
SELECT ('95010000-0000-0000-0000-' || LPAD((200 + value)::text, 12, '0'))::uuid,
       'V0039-NONPREFIX-RESERVED-' || value,
       'V0039-RESERVED-HASH-' || value,
       '04600000009501', '95010000-0000-0000-0000-000000000001',
       '95010000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic',
       '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'
FROM generate_series(1, 2) value;

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id,
                         status, origin, created_at, updated_at)
SELECT ('95010000-0000-0000-0000-' || LPAD((300 + value)::text, 12, '0'))::uuid,
       'V0039-NONPREFIX-APPLIED-' || value,
       'V0039-APPLIED-HASH-' || value,
       '04600000009501', '95010000-0000-0000-0000-000000000002',
       '95010000-0000-0000-0000-000000000102', 'Applied', 'LegacySynthetic',
       '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'
FROM generate_series(1, 5) value;
");
    }

    private static void SeedRepresentativeRetirementShape(
        NpgsqlConnection connection,
        RepresentativeRetirementShape shape,
        int index)
    {
        var catalogId = 9601 + index;
        var orderId = 9701 + index;
        var gtin = $"0460000000{catalogId:D4}";
        using (var scope = connection.CreateCommand())
        {
            scope.CommandText = @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog,
                       enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (@catalog_id, @name, @code, 1, TRUE, TRUE, FALSE, FALSE, TRUE);
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (@catalog_id, @name, @barcode, @gtin, @catalog_id);
INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (@order_id, @order_ref, 'INTERNAL', 'ACCEPTED', '2026-08-25T09:00:00.000Z', 'FLOWSTOCK');
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (@line_id, @order_id, @catalog_id, @target_quantity);";
            scope.Parameters.AddWithValue("@catalog_id", catalogId);
            scope.Parameters.AddWithValue("@order_id", orderId);
            scope.Parameters.AddWithValue("@line_id", shape.LineId);
            scope.Parameters.AddWithValue("@target_quantity", shape.AppliedQuantities.Sum());
            scope.Parameters.AddWithValue("@name", $"V0039 representative {shape.LineId}");
            scope.Parameters.AddWithValue("@code", $"V0039-REP-TYPE-{shape.LineId}");
            scope.Parameters.AddWithValue("@barcode", $"V0039-REP-ITEM-{shape.LineId}");
            scope.Parameters.AddWithValue("@gtin", gtin);
            scope.Parameters.AddWithValue("@order_ref", $"V0039-REP-ORDER-{shape.LineId}");
            scope.ExecuteNonQuery();
        }

        InsertRepresentativeTask(
            connection,
            shape.LineId,
            orderId,
            catalogId,
            gtin,
            shape.CandidateId,
            taskIndex: 0,
            appliedQuantity: 0,
            reservedQuantity: 1);
        for (var taskIndex = 0; taskIndex < shape.AppliedQuantities.Length; taskIndex++)
        {
            InsertRepresentativeTask(
                connection,
                shape.LineId,
                orderId,
                catalogId,
                gtin,
                Guid.Parse($"{shape.LineId:D8}-0000-0000-0000-{taskIndex + 1:D12}"),
                taskIndex + 1,
                shape.AppliedQuantities[taskIndex],
                taskIndex == 0 ? shape.RemainingReservedQuantity : 0);
        }
    }

    private static void InsertRepresentativeTask(
        NpgsqlConnection connection,
        long lineId,
        long orderId,
        long itemId,
        string gtin,
        Guid taskId,
        int taskIndex,
        int appliedQuantity,
        int reservedQuantity)
    {
        var requestedQuantity = appliedQuantity + reservedQuantity;
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO marking_order(id, order_id, item_id, gtin, requested_quantity, request_number,
                          status, source_type, source_order_id, request_status, created_at, updated_at)
VALUES (@task_id, @order_id, @item_id, @gtin, @requested_quantity, @request_number,
        'Printed', 'PRODUCTION_ORDER', @order_id, 'NotRequested',
        '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z');
INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type,
                                matched_marking_order_id, status, imported_rows, valid_code_rows,
                                duplicate_code_rows, created_at, processed_at)
VALUES ((md5('representative-import:' || @task_id::text))::uuid, 'synthetic-fixture.xlsx',
        '<temporary-chz-export>', @file_hash, 'temporary-chz-export', @task_id, 'Imported',
        @requested_quantity, @requested_quantity, 0,
        '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z');
INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id,
                         status, origin, created_at, updated_at)
SELECT (md5('representative-applied:' || @task_id::text || ':' || value::text))::uuid,
       'V0039-REP-APPLIED-' || @line_id::text || '-' || @task_index::text || '-' || value::text,
       md5('representative-applied-hash:' || @task_id::text || ':' || value::text),
       @gtin, @task_id, (md5('representative-import:' || @task_id::text))::uuid,
       'Applied', 'LegacySynthetic', '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'
FROM generate_series(1, @applied_quantity) value;
INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id,
                         status, origin, created_at, updated_at)
SELECT (md5('representative-reserved:' || @task_id::text || ':' || value::text))::uuid,
       'V0039-REP-RESERVED-' || @line_id::text || '-' || @task_index::text || '-' || value::text,
       md5('representative-reserved-hash:' || @task_id::text || ':' || value::text),
       @gtin, @task_id, (md5('representative-import:' || @task_id::text))::uuid,
       'Reserved', 'LegacySynthetic', '2026-08-25T09:00:00.000Z', '2026-08-25T09:00:00.000Z'
FROM generate_series(1, @reserved_quantity) value;";
        command.Parameters.AddWithValue("@task_id", taskId);
        command.Parameters.AddWithValue("@order_id", orderId);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@gtin", gtin);
        command.Parameters.AddWithValue("@requested_quantity", requestedQuantity);
        command.Parameters.AddWithValue("@request_number", $"V0039-REP-{lineId}-{taskIndex}");
        command.Parameters.AddWithValue("@file_hash", $"V0039-REP-IMPORT-{lineId}-{taskIndex}");
        command.Parameters.AddWithValue("@line_id", lineId);
        command.Parameters.AddWithValue("@task_index", taskIndex);
        command.Parameters.AddWithValue("@applied_quantity", appliedQuantity);
        command.Parameters.AddWithValue("@reserved_quantity", reservedQuantity);
        command.ExecuteNonQuery();
    }

    private sealed record RepresentativeRetirementShape(
        long LineId,
        Guid CandidateId,
        int[] AppliedQuantities,
        int RemainingReservedQuantity);

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // The cutover regression suite uses its own dedicated connection variable and never falls back
    // to FLOWSTOCK_POSTGRES_CONNECTION / POSTGRES_CONNECTION_STRING, so it can never accidentally
    // run DELETE against a shared or production database.
    private static string? ResolveCutoverTestConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("FLOWSTOCK_MARKING_CUTOVER_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
