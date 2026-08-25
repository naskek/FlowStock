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
            if (AssertV0040IgnoresSyntheticTaskHistory(connection))
            {
                return;
            }
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
            if (AssertV0040IgnoresSyntheticTaskHistory(connection))
            {
                return;
            }
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
            if (AssertV0040IgnoresSyntheticTaskHistory(connection))
            {
                return;
            }
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
            if (AssertV0040IgnoresSyntheticTaskHistory(connection))
            {
                return;
            }
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

            if (AssertV0040IgnoresSyntheticTaskHistory(connection))
            {
                return;
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

            Assert.DoesNotContain(entries, entry =>
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
    public void CompletedFilledClosed_WithSufficientLedger_IsFrozenWithoutFakeReadyHuFact()
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
FROM marking_legacy_cutover_line_scope
WHERE order_id = 9401 AND order_line_id = 9401;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT COUNT(*)
FROM marking_ready_hu_fact fact
INNER JOIN marking_production_subject subject ON subject.id = fact.marking_subject_id
WHERE subject.current_order_id = 9401;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT COUNT(*)
FROM marking_operational_coverage
WHERE source_type <> 'REAL_IMPORT';"));
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
    public void CoherentFilledPallet_WithOpenProductionDocument_IsFrozenLegacyCandidate()
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

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.Level == "error");
        });
    }

    [Fact]
    public void FilledClosedPallet_WithMismatchedCurrentDocLine_RemainsFailClosed()
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
SET current_doc_line_id = NULL
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
    [InlineData(false)]
    [InlineData(true)]
    public void PrintedPallet_WithExactComponentLineage_IgnoresLegacyCurrentDocSnapshot(bool useStaleDoc)
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
            if (useStaleDoc)
            {
                AddLegacyProductionDocument(connection, 9402);
            }
            Execute(connection, $@"
UPDATE marking_production_subject
SET current_doc_id = {(useStaleDoc ? "9402" : "NULL")}
WHERE current_order_id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN");
        });
    }

    [Fact]
    public void FilledPallet_WithExactCompletedComponentLineage_IgnoresLegacyCurrentDocSnapshot()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            AddLegacyProductionDocument(connection, 9402);
            Execute(connection, @"
UPDATE marking_production_subject
SET current_doc_id = 9402
WHERE current_order_id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode is "MARKING_ACTIVE_PALLET_PLAN" or "MARKING_FILLING_PROGRESS");
        });
    }

    [Fact]
    public void PrintedSharedPallet_WithExactComponentLineage_IgnoresOtherComponentHeaderSnapshot()
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
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9402, 'TEST-CUTOVER-HISTORY-HEADER-ITEM', 'TEST-CUTOVER-HISTORY-HEADER-ITEM',
        '04600000009402', 9401);
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9402, 9401, 9402, 50);
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (9402, 9401, 9402, 9402, 50, 9401, 'TEST-CUTOVER-HISTORY-HU');
UPDATE production_pallets
SET order_line_id = NULL,
    item_id = 9402,
    doc_line_id = 9402
WHERE id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN");
        });
    }

    [Fact]
    public void PrintedPallet_WithComponentDocLineOwnedByAnotherPrd_RemainsFailClosed()
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
            AddLegacyProductionDocument(connection, 9402);
            Execute(connection, @"
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (9402, 9402, 9401, 9401, 100, 9401, 'TEST-CUTOVER-HISTORY-HU');
UPDATE production_pallet_lines
SET doc_line_id = 9402
WHERE id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
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
    public void PlannedOrPrintedPallet_WithExactActiveSubject_IsCoherentLegacyScope(
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

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN");
            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.IssueCode == "MARKING_FILLING_PROGRESS");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_SUBJECT_SNAPSHOT"
                && entry.Level == "info");
            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_OPEN_PRD"
                && entry.Level == "warning");
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

    [Theory]
    [InlineData("current_order_line_id = NULL")]
    [InlineData("current_production_pallet_id = NULL")]
    [InlineData("item_id = 9402")]
    [InlineData("gtin = '04600000009402'")]
    [InlineData("subject_quantity = subject_quantity - 1")]
    public void PrintedPallet_WithMismatchedCurrentSubject_RemainsFailClosed(string mutation)
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
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9402, 'TEST-CUTOVER-HISTORY-OTHER-ITEM', 'TEST-CUTOVER-HISTORY-OTHER-ITEM',
        '04600000009402', 9401);");
            Execute(connection, $@"
UPDATE marking_production_subject
SET {mutation}
WHERE current_order_id = 9401;");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Fact]
    public void PrintedPallet_WithAmbiguousCurrentSubjects_RemainsFailClosed()
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
INSERT INTO marking_production_subject(
    id, lifecycle, current_production_pallet_id, current_component_id,
    current_doc_id, current_doc_line_id, current_order_id, current_order_line_id,
    item_id, gtin, subject_quantity, created_at)
VALUES ('94010000-0000-0000-0000-000000000099', 'ACTIVE', 9401, 9401,
        9401, 9401, 9401, 9401, 9401, '04600000009401', 100,
        '2026-08-24T10:00:00.000Z');");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.Level == "error");
        });
    }

    [Fact]
    public void FilledPallet_WithExactCompletedSubjectAndCompleteQuantity_IsCoherentLegacyScope()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode is "MARKING_ACTIVE_PALLET_PLAN" or "MARKING_FILLING_PROGRESS");
        });
    }

    [Fact]
    public void FilledPallet_WithPartialQuantity_RemainsFillingBlocker()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 50,
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
    public void MultipleFilledAndPrintedPallets_WithExactSubjects_AreOneCoherentLegacyScope()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "FILLED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 100,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            Execute(connection, "UPDATE order_lines SET qty_ordered = 300 WHERE id = 9401;");
            AddMarkingPallet(
                connection,
                palletId: 9402,
                palletStatus: "FILLED",
                plannedQuantity: 100,
                filledQuantity: 100);
            AddMarkingPallet(
                connection,
                palletId: 9403,
                palletStatus: "PRINTED",
                plannedQuantity: 100,
                filledQuantity: 0);

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Equal(3, ExecuteScalarInt(connection, @"
SELECT COUNT(*)
FROM production_pallet_lines component
INNER JOIN marking_production_subject subject ON subject.id = component.marking_subject_id
WHERE component.order_line_id = 9401
  AND subject.current_order_line_id = 9401
  AND subject.current_production_pallet_id = component.production_pallet_id
  AND subject.item_id = component.item_id
  AND subject.gtin = '04600000009401'
  AND subject.subject_quantity = component.planned_qty;"));
            Assert.DoesNotContain(entries, entry =>
                entry.OrderId == 9401
                && entry.OrderLineId == 9401
                && entry.IssueCode is "MARKING_ACTIVE_PALLET_PLAN" or "MARKING_FILLING_PROGRESS");
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

            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_", StringComparison.Ordinal));
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

            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_", StringComparison.Ordinal)
                || entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT");

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

            if (AssertV0040IgnoresSyntheticTaskHistory(connection))
            {
                return;
            }

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

            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_", StringComparison.Ordinal));
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

            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_", StringComparison.Ordinal)
                || entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT");
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

    [Fact]
    public void V0040_OutboundAttribution_RealReadyDoesNotConsumeFrozenLegacyFulfillment()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedV0040OutboundScenario(connection);
            var store = new PostgresDataStore(connection.ConnectionString);

            Assert.Equal("APPLIED", ExecuteScalarString(connection,
                "SELECT calculate_order_marking_status(9701);"));
            store.ValidateFinalMarkingHuBinding(9701,
                new Dictionary<string, double> { ["V0040-LEGACY-HU"] = 100 });
            store.ValidateFinalMarkingHuBinding(9701,
                new Dictionary<string, double> { ["V0040-REAL-HU"] = 50 });
            var newOrderError = Assert.Throws<InvalidOperationException>(() =>
                store.ValidateFinalMarkingHuBinding(9702,
                    new Dictionary<string, double> { ["V0040-LEGACY-HU"] = 100 }));
            Assert.Equal("MARKING_HU_REAL_ELIGIBILITY_REQUIRED", newOrderError.Message);

            var documents = new DocumentService(store);
            var realClose = documents.TryCloseDoc(9711, allowNegative: false);
            Assert.True(realClose.Success, string.Join(" | ", realClose.Errors));
            Assert.Equal(MarkingOutboundBasis.RealReady, ExecuteScalarString(connection, @"
SELECT basis FROM marking_outbound_fulfillment_attribution
WHERE outbound_doc_line_id = 9711;"));

            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT frozen_unshipped_legacy_quantity - COALESCE((
    SELECT SUM(quantity)
    FROM marking_outbound_fulfillment_attribution attribution
    WHERE attribution.frozen_line_scope_id = scope.id
      AND attribution.basis = 'LEGACY_EXEMPT'), 0)
FROM marking_legacy_cutover_line_scope scope
WHERE scope.order_line_id = 9701;"));
            Assert.Equal("APPLIED", ExecuteScalarString(connection,
                "SELECT calculate_order_marking_status(9701);"));

            var legacyClose = documents.TryCloseDoc(9712, allowNegative: false);
            Assert.True(legacyClose.Success, string.Join(" | ", legacyClose.Errors));
            Assert.Equal(MarkingOutboundBasis.LegacyExempt, ExecuteScalarString(connection, @"
SELECT basis FROM marking_outbound_fulfillment_attribution
WHERE outbound_doc_line_id = 9712;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT frozen_unshipped_legacy_quantity - COALESCE((
    SELECT SUM(quantity)
    FROM marking_outbound_fulfillment_attribution attribution
    WHERE attribution.frozen_line_scope_id = scope.id
      AND attribution.basis = 'LEGACY_EXEMPT'), 0)
FROM marking_legacy_cutover_line_scope scope
WHERE scope.order_line_id = 9701;"));

            _ = documents.TryCloseDoc(9711, allowNegative: false);
            _ = documents.TryCloseDoc(9712, allowNegative: false);
            Assert.Equal(2, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_outbound_fulfillment_attribution;"));
            Assert.Equal(1, ExecuteScalarInt(connection, @"
SELECT COUNT(*) FROM marking_outbound_fulfillment_attribution
WHERE basis = 'REAL_READY';"));
            Assert.Equal(1, ExecuteScalarInt(connection, @"
SELECT COUNT(*) FROM marking_outbound_fulfillment_attribution
WHERE basis = 'LEGACY_EXEMPT';"));

            Assert.Throws<InvalidOperationException>(() =>
                store.ValidateFinalMarkingHuBinding(9701,
                    new Dictionary<string, double> { ["V0040-LEGACY-HU"] = 100 }));

            Execute(connection, @"
UPDATE marking_ready_hu_fact
SET reversed_at = '2026-08-25T12:00:00.000Z', correction_reference = 'TEST-V0040-REVERSAL'
WHERE id = '97010000-0000-0000-0000-000000000040';");
            Assert.Equal("REAL_READY", ExecuteScalarString(connection, @"
SELECT basis FROM marking_outbound_fulfillment_attribution
WHERE outbound_doc_line_id = 9711;"));
            var reversedError = Assert.Throws<InvalidOperationException>(() =>
                store.ValidateFinalMarkingHuBinding(9701,
                    new Dictionary<string, double> { ["V0040-REAL-HU"] = 50 }));
            Assert.Equal("MARKING_HU_REAL_ELIGIBILITY_REQUIRED", reversedError.Message);

            var obsoleteLineApproval = Assert.Throws<InvalidOperationException>(() =>
                store.ApproveMarkingCutoverLine(
                    9701, null, "TEST-V0040-PREFLIGHT", "SERVER:test", DateTime.UtcNow));
            Assert.Equal("MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE", obsoleteLineApproval.Message);
            var obsoleteSubjectApproval = Assert.Throws<InvalidOperationException>(() =>
                store.ApproveMarkingCutoverSubjects(
                    1,
                    [new MarkingCutoverSubjectApprovalIntent(
                        Guid.Parse("97010000-0000-0000-0000-000000000030"), 1)],
                    "TEST-V0040-PREFLIGHT",
                    "SERVER:test",
                    DateTime.UtcNow));
            Assert.Equal("MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE", obsoleteSubjectApproval.Message);
            var obsoleteRetirement = Assert.Throws<InvalidOperationException>(() =>
                store.Apply(
                    9701,
                    Guid.Parse("97010000-0000-0000-0000-000000000099"),
                    "TEST-V0040-PREFLIGHT",
                    "TEST-V0040-ELIGIBILITY",
                    "TEST-V0040-RETIREMENT",
                    "SERVER:test",
                    DateTime.UtcNow));
            Assert.Equal("MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE", obsoleteRetirement.Message);
        });
    }

    [Fact]
    public void V0040_MixedPhysicalHu_WithFullRealFact_IsRejectedByBindingAndOutboundWithoutWrites()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedV0040OutboundScenario(connection);
            AddV0040MixedPhysicalComposition(connection, "V0040-REAL-HU");
            var store = new PostgresDataStore(connection.ConnectionString);

            var bindingError = Assert.Throws<InvalidOperationException>(() =>
                store.ValidateFinalMarkingHuBinding(9701,
                    new Dictionary<string, double> { ["V0040-REAL-HU"] = 50 }));
            Assert.Equal("MARKING_HU_REAL_ELIGIBILITY_AMBIGUOUS", bindingError.Message);

            var decisionError = Assert.Throws<InvalidOperationException>(() =>
                store.DecideOutboundMarkingEligibility(
                    9711,
                    "SERVER:test",
                    new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)));
            Assert.Equal("MARKING_HU_REAL_ELIGIBILITY_AMBIGUOUS", decisionError.Message);

            var ledgerBefore = ExecuteScalarString(connection, @"
SELECT md5(string_agg(item_id::text || ':' || location_id::text || ':' ||
                      qty_delta::text || ':' || COALESCE(hu_code, ''),
                      ',' ORDER BY id))
FROM ledger;");
            var close = new DocumentService(store).TryCloseDoc(9711, allowNegative: false);

            Assert.False(close.Success);
            Assert.Equal("DRAFT", ExecuteScalarString(connection,
                "SELECT status FROM docs WHERE id = 9711;"));
            Assert.Equal(ledgerBefore, ExecuteScalarString(connection, @"
SELECT md5(string_agg(item_id::text || ':' || location_id::text || ':' ||
                      qty_delta::text || ':' || COALESCE(hu_code, ''),
                      ',' ORDER BY id))
FROM ledger;"));
            Assert.Equal(0, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_outbound_fulfillment_attribution;"));
        });
    }

    [Fact]
    public void V0040_MixedPhysicalHu_WithLegacyAllowance_IsRejectedByOutboundWithoutWrites()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedV0040OutboundScenario(connection);
            AddV0040MixedPhysicalComposition(connection, "V0040-LEGACY-HU");
            var store = new PostgresDataStore(connection.ConnectionString);

            var decisionError = Assert.Throws<InvalidOperationException>(() =>
                store.DecideOutboundMarkingEligibility(
                    9712,
                    "SERVER:test",
                    new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)));
            Assert.Equal("MARKING_HU_REAL_ELIGIBILITY_AMBIGUOUS", decisionError.Message);

            var ledgerBefore = ExecuteScalarString(connection, @"
SELECT md5(string_agg(item_id::text || ':' || location_id::text || ':' ||
                      qty_delta::text || ':' || COALESCE(hu_code, ''),
                      ',' ORDER BY id))
FROM ledger;");
            var close = new DocumentService(store).TryCloseDoc(9712, allowNegative: false);

            Assert.False(close.Success);
            Assert.Equal("DRAFT", ExecuteScalarString(connection,
                "SELECT status FROM docs WHERE id = 9712;"));
            Assert.Equal(ledgerBefore, ExecuteScalarString(connection, @"
SELECT md5(string_agg(item_id::text || ':' || location_id::text || ':' ||
                      qty_delta::text || ':' || COALESCE(hu_code, ''),
                      ',' ORDER BY id))
FROM ledger;"));
            Assert.Equal(0, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_outbound_fulfillment_attribution;"));
        });
    }

    [Fact]
    public void V0040_FinalBinding_NonexistentOrderLineFailsAsStaleContext()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedV0040OutboundScenario(connection);
            var store = new PostgresDataStore(connection.ConnectionString);

            var error = Assert.Throws<InvalidOperationException>(() =>
                store.ValidateFinalMarkingHuBinding(9799,
                    new Dictionary<string, double> { ["V0040-REAL-HU"] = 50 }));

            Assert.Equal("MARKING_HU_ELIGIBILITY_CHANGED", error.Message);

            Execute(connection, @"
INSERT INTO item_types(id, name, code, enable_marking)
VALUES (9798, 'TEST-V0040-NON-MARKING-TYPE', 'TEST-V0040-NON-MARKING-TYPE', FALSE);
INSERT INTO items(id, name, barcode, item_type_id)
VALUES (9798, 'TEST-V0040-NON-MARKING-ITEM', 'TEST-V0040-NON-MARKING-ITEM', 9798);
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9798, 9702, 9798, 1);");

            store.ValidateFinalMarkingHuBinding(9798,
                new Dictionary<string, double> { ["V0040-REAL-HU"] = 50 });
        });
    }

    [Fact]
    public void V0040_RepresentativeTwelveLinesAndTwentySixSubjects_FormFrozenCohortWithoutCoverage()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, enable_marking)
VALUES (9800, 'TEST-V0040-REP-TYPE', 'TEST-V0040-REP-TYPE', TRUE);
INSERT INTO locations(id, code, name)
VALUES (9800, 'TEST-V0040-REP-LOC', 'TEST-V0040-REP-LOC');
INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9800, 'TEST-V0040-REP-ORDER', 'INTERNAL', 'ACCEPTED',
        '2026-08-25T08:00:00.000Z', 'FLOWSTOCK');
INSERT INTO docs(id, doc_ref, type, status, created_at, order_id, order_ref)
VALUES (9800, 'TEST-V0040-REP-PRD', 'PRODUCTION_RECEIPT', 'DRAFT',
        '2026-08-25T08:00:00.000Z', 9800, 'TEST-V0040-REP-ORDER');");

            var subjectOrdinal = 0;
            for (var lineOrdinal = 1; lineOrdinal <= 12; lineOrdinal++)
            {
                var itemId = 9800 + lineOrdinal;
                var lineId = 9800 + lineOrdinal;
                var subjectCount = lineOrdinal <= 2 ? 3 : 2;
                using (var line = connection.CreateCommand())
                {
                    line.CommandText = @"
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (@item_id, @name, @barcode, @gtin, 9800);
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (@line_id, 9800, @item_id, @quantity);";
                    line.Parameters.AddWithValue("@item_id", itemId);
                    line.Parameters.AddWithValue("@line_id", lineId);
                    line.Parameters.AddWithValue("@name", $"TEST-V0040-REP-ITEM-{lineOrdinal}");
                    line.Parameters.AddWithValue("@barcode", $"TEST-V0040-REP-BARCODE-{lineOrdinal}");
                    line.Parameters.AddWithValue("@gtin", $"0460000000{lineOrdinal:D4}");
                    line.Parameters.AddWithValue("@quantity", subjectCount);
                    line.ExecuteNonQuery();
                }

                for (var localSubject = 0; localSubject < subjectCount; localSubject++)
                {
                    subjectOrdinal++;
                    var rowId = 980000 + subjectOrdinal;
                    var hu = $"TEST-V0040-REP-HU-{subjectOrdinal:D2}";
                    using var subject = connection.CreateCommand();
                    subject.CommandText = @"
INSERT INTO hus(hu_code, status, created_at)
VALUES (@hu, 'ACTIVE', '2026-08-25T08:00:00.000Z');
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (@row_id, 9800, @line_id, @item_id, 1, 9800, @hu);
INSERT INTO production_pallets(
    id, prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
    hu_code, planned_qty, to_location_id, status, created_at)
VALUES (@row_id, 9800, @row_id, 9800, @line_id, @item_id,
        @hu, 1, 9800, 'PLANNED', '2026-08-25T08:00:00.000Z');
INSERT INTO production_pallet_lines(
    id, production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES (@row_id, @row_id, @row_id, @line_id, @item_id,
        1, 0, '2026-08-25T08:00:00.000Z');";
                    subject.Parameters.AddWithValue("@row_id", rowId);
                    subject.Parameters.AddWithValue("@line_id", lineId);
                    subject.Parameters.AddWithValue("@item_id", itemId);
                    subject.Parameters.AddWithValue("@hu", hu);
                    subject.ExecuteNonQuery();
                }
            }

            Assert.Equal(26, subjectOrdinal);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            Assert.Equal(12, preflight.LegacyLineSnapshots!.Count);
            Assert.Equal(26, preflight.LegacySubjectSnapshots!.Count);
            Assert.DoesNotContain(preflight.Entries, entry =>
                string.Equals(entry.Level, "error", StringComparison.OrdinalIgnoreCase));

            store.EnforceMarkingCutover(preflight.Hash, "SERVER:test", DateTime.UtcNow);
            Assert.Equal(12, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_legacy_cutover_line_scope;"));
            Assert.Equal(26, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal(26, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal(0, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_operational_coverage;"));
            Assert.Equal(0, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_ready_hu_fact;"));
            Assert.Equal(0, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_code;"));
        });
    }

    [Fact]
    public void V0040_OutboundAttribution_LegacyThenRealHasSameFinalAccounting()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedV0040OutboundScenario(connection);
            var documents = new DocumentService(new PostgresDataStore(connection.ConnectionString));

            var legacyClose = documents.TryCloseDoc(9712, allowNegative: false);
            Assert.True(legacyClose.Success, string.Join(" | ", legacyClose.Errors));
            var realClose = documents.TryCloseDoc(9711, allowNegative: false);
            Assert.True(realClose.Success, string.Join(" | ", realClose.Errors));

            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT SUM(quantity) FROM marking_outbound_fulfillment_attribution
WHERE basis = 'LEGACY_EXEMPT';"));
            Assert.Equal(50, ExecuteScalarInt(connection, @"
SELECT SUM(quantity) FROM marking_outbound_fulfillment_attribution
WHERE basis = 'REAL_READY';"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT frozen_unshipped_legacy_quantity - COALESCE((
    SELECT SUM(quantity)
    FROM marking_outbound_fulfillment_attribution attribution
    WHERE attribution.frozen_line_scope_id = scope.id
      AND attribution.basis = 'LEGACY_EXEMPT'), 0)
FROM marking_legacy_cutover_line_scope scope
WHERE scope.order_line_id = 9701;"));
        });
    }

    [Fact]
    public void V0040_OutboundAttributionFailure_RollsBackDocumentLedgerAndAttribution()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedV0040OutboundScenario(connection);
            Execute(connection, @"
CREATE OR REPLACE FUNCTION test_v0040_reject_attribution()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'TEST_V0040_ATTRIBUTION_FAILURE';
END;
$$;
CREATE TRIGGER test_v0040_reject_attribution
BEFORE INSERT ON marking_outbound_fulfillment_attribution
FOR EACH ROW EXECUTE FUNCTION test_v0040_reject_attribution();");

            try
            {
                var documents = new DocumentService(new PostgresDataStore(connection.ConnectionString));
                var exception = Assert.ThrowsAny<Exception>(() =>
                    documents.TryCloseDoc(9711, allowNegative: false));
                Assert.Contains("TEST_V0040_ATTRIBUTION_FAILURE", exception.ToString(), StringComparison.Ordinal);

                Assert.Equal("DRAFT", ExecuteScalarString(connection,
                    "SELECT status FROM docs WHERE id = 9711;"));
                Assert.Equal(2, ExecuteScalarInt(connection,
                    "SELECT COUNT(*) FROM ledger;"));
                Assert.Equal(50, ExecuteScalarInt(connection, @"
SELECT SUM(qty_delta) FROM ledger
WHERE item_id = 9701 AND hu_code = 'V0040-REAL-HU';"));
                Assert.Equal(0, ExecuteScalarInt(connection,
                    "SELECT COUNT(*) FROM marking_outbound_fulfillment_attribution;"));
            }
            finally
            {
                Execute(connection, @"
DROP TRIGGER IF EXISTS test_v0040_reject_attribution
ON marking_outbound_fulfillment_attribution;
DROP FUNCTION IF EXISTS test_v0040_reject_attribution();");
            }
        });
    }

    [Fact]
    public void V0040_SafeDecreaseAndIncrease_RebalancesSubjectExemptionWithinFrozenCap()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PLANNED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            store.EnforceMarkingCutover(preflight.Hash, "SERVER:test", DateTime.UtcNow);

            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT active_quantity FROM marking_legacy_cutover_subject_exemption;"));

            Execute(connection, @"
UPDATE order_lines SET qty_ordered = 80 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 80 WHERE id = 9401;");
            Assert.Equal(80, ExecuteScalarInt(connection, @"
SELECT active_quantity FROM marking_legacy_cutover_subject_exemption;"));

            Execute(connection, @"
UPDATE order_lines SET qty_ordered = 100 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 100 WHERE id = 9401;");
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT active_quantity FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT frozen_quantity FROM marking_legacy_cutover_line_scope WHERE order_line_id = 9401;"));

            Execute(connection, @"
UPDATE order_lines
SET cancelled_at = '2026-08-25T13:00:00.000Z'
WHERE id = 9401;");
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT active_quantity FROM marking_legacy_cutover_subject_exemption;"));
        });
    }

    [Fact]
    public void V0040_DecreaseWithRealRequest_RetiresConsumablesWithoutRestoringThemOnIncrease()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PLANNED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            store.EnforceMarkingCutover(preflight.Hash, "SERVER:test", DateTime.UtcNow);

            Execute(connection, @"
UPDATE order_lines SET qty_ordered = 120 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 120 WHERE id = 9401;");
            var firstExport = new OrderMarkingExportService(store).Export(9401, DateTime.UtcNow);
            Assert.True(firstExport.IsSuccess, firstExport.Message);
            Assert.Equal(20, Assert.Single(firstExport.Lines).ExportQty);

            Execute(connection, @"
WITH request AS (
    SELECT scope.id AS scope_id, scope.marking_subject_id,
           request.id AS request_id, request.order_id, request.order_line_id
    FROM marking_request_scope scope
    INNER JOIN marking_order request ON request.id = scope.marking_order_id
    WHERE request.order_id = 9401
    ORDER BY scope.created_at
    LIMIT 1
), batch AS (
    INSERT INTO marking_import_batch(
        id, order_id, order_line_id, marking_order_id, original_filename,
        file_hash, file_size_bytes, row_count, status,
        target_marking_qty_snapshot, coverage_snapshot_hash,
        idempotency_key, created_at, confirmed_at)
    SELECT '94010000-0000-0000-0000-000000000401', order_id, order_line_id,
           request_id, 'test-v0040-real.tsv', 'TEST-V0040-REAL-FILE', 20, 20,
           'Confirmed', 20, 'TEST-V0040-REAL-SNAPSHOT',
           'test-v0040-real-confirm', '2026-08-25T12:00:00.000Z',
           '2026-08-25T12:00:00.000Z'
    FROM request
    RETURNING id
)
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, marking_request_scope_id, source_type,
    covered_quantity, import_batch_id, created_at)
SELECT '94010000-0000-0000-0000-000000000402', request.marking_subject_id,
       request.scope_id, 'REAL_IMPORT', 20, batch.id, '2026-08-25T12:00:00.000Z'
FROM request CROSS JOIN batch;");
            Assert.Equal("APPLIED", ExecuteScalarString(connection,
                "SELECT calculate_order_marking_status(9401);"));

            Execute(connection, @"
BEGIN;
UPDATE order_lines SET qty_ordered = 80 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 80 WHERE id = 9401;
COMMIT;");
            Assert.Equal(80, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_request_scope_consumption;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_operational_coverage_consumption;"));
            Assert.Equal(20, ExecuteScalarInt(connection, @"
SELECT covered_quantity FROM marking_operational_coverage;"));
            Assert.Equal("NOT_REQUIRED", ExecuteScalarString(connection,
                "SELECT calculate_order_marking_status(9401);"));

            Execute(connection, @"
BEGIN;
UPDATE order_lines SET qty_ordered = 120 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 120 WHERE id = 9401;
COMMIT;");
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_request_scope_consumption;"));
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity) FROM marking_operational_coverage_consumption;"));
            Assert.Equal("NOT_APPLIED", ExecuteScalarString(connection,
                "SELECT calculate_order_marking_status(9401);"));

            var secondExport = new OrderMarkingExportService(store).Export(9401, DateTime.UtcNow.AddMinutes(1));
            Assert.True(secondExport.IsSuccess, secondExport.Message);
            Assert.Equal(20, Assert.Single(secondExport.Lines).ExportQty);
            Assert.Equal(2, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_order WHERE order_id = 9401;"));
            Assert.Equal(2, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_request_scope scope INNER JOIN marking_order request ON request.id = scope.marking_order_id WHERE request.order_id = 9401;"));
        });
    }

    [Fact]
    public void V0040_SafeReplan_GrantsOnlyBoundedExemptionToNewCanonicalSubject()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PLANNED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            store.EnforceMarkingCutover(preflight.Hash, "SERVER:test", DateTime.UtcNow);

            Execute(connection, "UPDATE production_pallets SET status = 'CANCELLED' WHERE id = 9401;");
            Assert.Equal(0, ExecuteScalarInt(connection, @"
SELECT COALESCE(SUM(active_quantity), 0)
FROM marking_legacy_cutover_subject_exemption;"));

            Execute(connection, @"
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (9402, 9401, 9401, 9401, 100, 9401, 'TEST-CUTOVER-REPLAN-HU');
INSERT INTO hus(hu_code, status, created_at)
VALUES ('TEST-CUTOVER-REPLAN-HU', 'ACTIVE', '2026-08-25T14:00:00.000Z');
INSERT INTO production_pallets(
    id, prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
    hu_code, planned_qty, to_location_id, status, created_at)
VALUES (9402, 9401, 9402, 9401, 9401, 9401,
        'TEST-CUTOVER-REPLAN-HU', 100, 9401, 'PLANNED', '2026-08-25T14:00:00.000Z');
INSERT INTO production_pallet_lines(
    id, production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES (9402, 9402, 9402, 9401, 9401, 100, 0, '2026-08-25T14:00:00.000Z');");

            Assert.Equal(2, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT COALESCE(SUM(active_quantity), 0)
FROM marking_legacy_cutover_subject_exemption;"));
            Assert.Equal("POST_CUTOVER_FROZEN_LINE_PLAN", ExecuteScalarString(connection, @"
SELECT exemption.basis
FROM marking_legacy_cutover_subject_exemption exemption
INNER JOIN production_pallet_lines component
        ON component.marking_subject_id = exemption.marking_subject_id
WHERE component.id = 9402;"));
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT exemption.granted_quantity
FROM marking_legacy_cutover_subject_exemption exemption
INNER JOIN production_pallet_lines component
        ON component.marking_subject_id = exemption.marking_subject_id
WHERE component.id = 9402;"));
        });
    }

    [Fact]
    public void V0040_NewIndependentSubject_UsesOnlyFreeCapacityAndPreservesExistingCutoverSubject()
    {
        RunMutatingPostgresTest(connection =>
        {
            var subjects = SeedV0040StableSubjectAllocationScenario(
                connection,
                newSubjectSortsBeforeExisting: false);

            AssertV0040StableSubjectAllocation(connection, subjects);
        });
    }

    [Fact]
    public void V0040_StableSubjectAllocation_IsIndependentOfSubjectUuidOrder()
    {
        RunMutatingPostgresTest(connection =>
        {
            var subjects = SeedV0040StableSubjectAllocationScenario(
                connection,
                newSubjectSortsBeforeExisting: true);

            AssertV0040StableSubjectAllocation(connection, subjects);
        });
    }

    [Fact]
    public void V0040_CancelledSubject_ReleasesCapacityOnlyToCanonicalCorrectionSuccessor()
    {
        RunMutatingPostgresTest(connection =>
        {
            var subjects = SeedV0040StableSubjectAllocationScenario(
                connection,
                newSubjectSortsBeforeExisting: true);
            var successorId = Guid.Parse("20000000-0000-0000-0000-000000000003");

            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE marking_production_subject
SET lifecycle = 'CANCELLED', cancelled_at = '2026-08-25T13:00:00.000Z'
WHERE id = @existing_subject_id;

INSERT INTO marking_production_subject(
    id, lifecycle, revision, predecessor_subject_id,
    original_production_pallet_id, original_component_id,
    original_order_id, original_order_line_id,
    current_production_pallet_id, current_component_id,
    current_order_id, current_order_line_id,
    item_id, gtin, subject_quantity, created_at)
VALUES (@successor_id, 'ACTIVE', 1, @existing_subject_id,
        9803, 9803, 9701, 9701, NULL, 9803, 9701, 9701,
        9701, '04600000009701', 90, '2026-08-25T13:00:00.000Z');

SELECT rebalance_marking_legacy_line_exemptions(
    9701, '2026-08-25T13:00:00.000Z', 'controlled_correction');";
            command.Parameters.AddWithValue("@existing_subject_id", subjects.ExistingSubjectId);
            command.Parameters.AddWithValue("@successor_id", successorId);
            command.ExecuteNonQuery();

            Assert.Equal(0, ReadV0040SubjectExemption(connection, subjects.ExistingSubjectId));
            Assert.Equal(40, ReadV0040SubjectExemption(connection, subjects.NewSubjectId));
            Assert.Equal(60, ReadV0040SubjectExemption(connection, successorId));
            Assert.Equal(60, ReadV0040SubjectExemptionGrant(connection, successorId));
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT SUM(active_quantity)
FROM marking_legacy_cutover_subject_exemption
WHERE frozen_line_scope_id = (
    SELECT id FROM marking_legacy_cutover_line_scope WHERE order_line_id = 9701);"));
            Assert.Equal(subjects.ExistingSubjectId.ToString(), ExecuteScalarString(connection, @"
SELECT root_subject_id::text
FROM marking_legacy_cutover_subject_exemption
WHERE marking_subject_id = '20000000-0000-0000-0000-000000000003';"));
        });
    }

    [Fact]
    public void V0040_IncreaseAboveFrozenCap_DoesNotMoveExistingSubjectExemptions()
    {
        RunMutatingPostgresTest(connection =>
        {
            var subjects = SeedV0040StableSubjectAllocationScenario(
                connection,
                newSubjectSortsBeforeExisting: true);

            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE order_lines SET qty_ordered = 200 WHERE id = 9701;
UPDATE marking_production_subject SET subject_quantity = 140 WHERE id = @new_subject_id;
SELECT rebalance_marking_legacy_line_exemptions(
    9701, '2026-08-25T14:00:00.000Z', 'line_increased_above_frozen_cap');";
            command.Parameters.AddWithValue("@new_subject_id", subjects.NewSubjectId);
            command.ExecuteNonQuery();

            Assert.Equal(60, ReadV0040SubjectExemption(connection, subjects.ExistingSubjectId));
            Assert.Equal(40, ReadV0040SubjectExemption(connection, subjects.NewSubjectId));
            Assert.Equal(100, ExecuteScalarInt(connection, @"
SELECT subject.subject_quantity - exemption.active_quantity
FROM marking_production_subject subject
INNER JOIN marking_legacy_cutover_subject_exemption exemption
        ON exemption.marking_subject_id = subject.id
WHERE subject.id = @new_subject_id;", ("@new_subject_id", subjects.NewSubjectId)));
        });
    }

    [Fact]
    public void V0040_SubjectAllocationMutationFault_RollsBackSubjectExemptionAndRealConsumables()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PLANNED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            var store = new PostgresDataStore(connection.ConnectionString);
            var preflight = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
            store.EnforceMarkingCutover(preflight.Hash, "SERVER:test", DateTime.UtcNow);

            Execute(connection, @"
UPDATE order_lines SET qty_ordered = 120 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 120 WHERE id = 9401;");
            var export = new OrderMarkingExportService(store).Export(9401, DateTime.UtcNow);
            Assert.True(export.IsSuccess, export.Message);

            Execute(connection, @"
WITH request AS (
    SELECT scope.id AS scope_id, scope.marking_subject_id,
           request.id AS request_id, request.order_id, request.order_line_id
    FROM marking_request_scope scope
    INNER JOIN marking_order request ON request.id = scope.marking_order_id
    WHERE request.order_id = 9401
    ORDER BY scope.created_at
    LIMIT 1
), batch AS (
    INSERT INTO marking_import_batch(
        id, order_id, order_line_id, marking_order_id, original_filename,
        file_hash, file_size_bytes, row_count, status,
        target_marking_qty_snapshot, coverage_snapshot_hash,
        idempotency_key, created_at, confirmed_at)
    SELECT '94010000-0000-0000-0000-000000000411', order_id, order_line_id,
           request_id, 'test-v0040-rollback.tsv', 'TEST-V0040-ROLLBACK-FILE', 20, 20,
           'Confirmed', 20, 'TEST-V0040-ROLLBACK-SNAPSHOT',
           'test-v0040-rollback-confirm', '2026-08-25T12:00:00.000Z',
           '2026-08-25T12:00:00.000Z'
    FROM request
    RETURNING id
)
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, marking_request_scope_id, source_type,
    covered_quantity, import_batch_id, created_at)
SELECT '94010000-0000-0000-0000-000000000412', request.marking_subject_id,
       request.scope_id, 'REAL_IMPORT', 20, batch.id, '2026-08-25T12:00:00.000Z'
FROM request CROSS JOIN batch;

CREATE OR REPLACE FUNCTION test_v0040_reject_deferred_status_update()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id = 9401 THEN
        RAISE EXCEPTION 'TEST_V0040_DEFERRED_MUTATION_FAILURE';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER test_v0040_reject_deferred_status_update
AFTER UPDATE ON orders
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION test_v0040_reject_deferred_status_update();");

            try
            {
                var exception = Assert.Throws<PostgresException>(() => Execute(connection, @"
BEGIN;
UPDATE order_lines SET qty_ordered = 80 WHERE id = 9401;
UPDATE production_pallet_lines SET planned_qty = 80 WHERE id = 9401;
COMMIT;"));
                Assert.Contains("TEST_V0040_DEFERRED_MUTATION_FAILURE", exception.MessageText, StringComparison.Ordinal);

                Assert.Equal(120, ExecuteScalarInt(connection,
                    "SELECT qty_ordered FROM order_lines WHERE id = 9401;"));
                Assert.Equal(120, ExecuteScalarInt(connection,
                    "SELECT planned_qty FROM production_pallet_lines WHERE id = 9401;"));
                Assert.Equal(120, ExecuteScalarInt(connection, @"
SELECT subject_quantity FROM marking_production_subject
WHERE id = (SELECT marking_subject_id FROM production_pallet_lines WHERE id = 9401);"));
                Assert.Equal(100, ExecuteScalarInt(connection,
                    "SELECT active_quantity FROM marking_legacy_cutover_subject_exemption;"));
                Assert.Equal(20, ExecuteScalarInt(connection,
                    "SELECT SUM(active_quantity) FROM marking_request_scope_consumption;"));
                Assert.Equal(20, ExecuteScalarInt(connection,
                    "SELECT SUM(active_quantity) FROM marking_operational_coverage_consumption;"));
            }
            finally
            {
                Execute(connection, @"
DROP TRIGGER IF EXISTS test_v0040_reject_deferred_status_update ON orders;
DROP FUNCTION IF EXISTS test_v0040_reject_deferred_status_update();");
            }
        });
    }

    [Fact]
    public void V0040_Enforce_ConcurrentLineDriftCannotCommitStaleCohort()
    {
        RunMutatingPostgresTest(connection =>
        {
            SeedMarkingPallet(
                connection,
                palletStatus: "PLANNED",
                documentStatus: "DRAFT",
                plannedQuantity: 100,
                filledQuantity: 0,
                hasPalletFilledAt: false,
                hasComponentFilledAt: false,
                ledgerQuantity: 0);
            var before = new MarkingCutoverPreflightService(
                new PostgresDataStore(connection.ConnectionString)).Run(DateTime.UtcNow);

            using var gateConnection = new NpgsqlConnection(connection.ConnectionString);
            gateConnection.Open();
            using var gateTransaction = gateConnection.BeginTransaction();
            using (var gate = gateConnection.CreateCommand())
            {
                gate.Transaction = gateTransaction;
                gate.CommandText = "SELECT state FROM marking_cutover_state WHERE id = TRUE FOR UPDATE;";
                _ = gate.ExecuteScalar();
            }

            var enforce = Task.Run<Exception?>(() =>
            {
                try
                {
                    new PostgresDataStore(connection.ConnectionString).EnforceMarkingCutover(
                        before.Hash, "SERVER:test", DateTime.UtcNow);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });
            Assert.True(SpinWait.SpinUntil(
                () => CountCutoverStateLockWaiters(connection.ConnectionString) >= 1,
                TimeSpan.FromSeconds(5)));

            using (var driftConnection = new NpgsqlConnection(connection.ConnectionString))
            {
                driftConnection.Open();
                Execute(driftConnection, "UPDATE order_lines SET qty_ordered = 120 WHERE id = 9401;");
            }
            gateTransaction.Commit();

            Assert.True(enforce.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotNull(enforce.Result);
            Assert.True(
                enforce.Result is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
                || enforce.Result is InvalidOperationException { Message: "MARKING_CUTOVER_PREFLIGHT_HASH_MISMATCH" },
                enforce.Result.ToString());
            Assert.Equal("SHADOW", ExecuteScalarString(connection,
                "SELECT state FROM marking_cutover_state WHERE id = TRUE;"));
            Assert.Equal(0, ExecuteScalarInt(connection,
                "SELECT COUNT(*) FROM marking_legacy_cutover_cohort;"));
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

    private static bool AssertV0040IgnoresSyntheticTaskHistory(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('marking_legacy_cutover_cohort') IS NOT NULL;";
        if (!Convert.ToBoolean(command.ExecuteScalar()))
        {
            return false;
        }

        var entries = ReadPreflightEntries(connection.ConnectionString);
        Assert.DoesNotContain(entries, entry =>
            entry.IssueCode.StartsWith("MARKING_LEGACY_", StringComparison.Ordinal)
            || entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT");
        return true;
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
TRUNCATE TABLE marking_legacy_cutover_cohort,
               ledger, production_pallet_lines, production_pallets, doc_lines, docs,
               orders, items, item_types, locations, hus, partners,
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

    private static void SeedV0040OutboundScenario(NpgsqlConnection connection)
    {
        Execute(connection, @"
INSERT INTO item_types(id, name, code, enable_marking)
VALUES (9701, 'TEST-V0040-TYPE', 'TEST-V0040-TYPE', TRUE);
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9701, 'TEST-V0040-ITEM', 'TEST-V0040-ITEM', '04600000009701', 9701);
INSERT INTO locations(id, code, name)
VALUES (9701, 'TEST-V0040-LOC', 'TEST-V0040-LOC');
INSERT INTO partners(id, name, code, created_at, partner_role)
VALUES (9701, 'TEST-V0040-PARTNER', 'TEST-V0040-PARTNER', '2026-08-25T10:00:00.000Z', 'CLIENT');
INSERT INTO hus(hu_code, status, created_at)
VALUES
('V0040-REAL-HU', 'ACTIVE', '2026-08-25T10:00:00.000Z'),
('V0040-LEGACY-HU', 'ACTIVE', '2026-08-25T10:00:00.000Z');
INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility, partner_id)
VALUES
(9701, 'TEST-V0040-FROZEN', 'CUSTOMER', 'ACCEPTED', '2026-08-25T10:00:00.000Z', 'FLOWSTOCK', 9701),
(9702, 'TEST-V0040-NEW', 'CUSTOMER', 'ACCEPTED', '2026-08-25T11:00:00.000Z', 'FLOWSTOCK', 9701);
INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES
(9701, 9701, 9701, 150),
(9702, 9702, 9701, 100);
INSERT INTO order_receipt_plan_lines(
    order_id, order_line_id, item_id, qty_planned, to_location_id, to_hu, sort_order)
VALUES
(9701, 9701, 9701, 50, 9701, 'V0040-REAL-HU', 1),
(9701, 9701, 9701, 100, 9701, 'V0040-LEGACY-HU', 2);

INSERT INTO docs(id, doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref)
VALUES
(9703, 'TEST-V0040-RECEIPT', 'PRODUCTION_RECEIPT', 'CLOSED',
 '2026-08-25T09:00:00.000Z', '2026-08-25T09:30:00.000Z', NULL, NULL, NULL),
(9711, 'TEST-V0040-REAL-OUT', 'OUTBOUND', 'DRAFT',
 '2026-08-25T11:00:00.000Z', NULL, 9701, 9701, 'TEST-V0040-FROZEN'),
(9712, 'TEST-V0040-LEGACY-OUT', 'OUTBOUND', 'DRAFT',
 '2026-08-25T11:10:00.000Z', NULL, 9701, 9701, 'TEST-V0040-FROZEN');
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (9703, 9703, NULL, 9701, 50, 9701, 'V0040-REAL-HU');
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, from_location_id, from_hu)
VALUES
(9711, 9711, 9701, 9701, 50, 9701, 'V0040-REAL-HU'),
(9712, 9712, 9701, 9701, 100, 9701, 'V0040-LEGACY-HU');
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code)
VALUES
('2026-08-25T09:30:00.000Z', 9703, 9701, 9701, 50, 'V0040-REAL-HU'),
('2026-08-25T09:30:00.000Z', 9703, 9701, 9701, 100, 'V0040-LEGACY-HU');

INSERT INTO marking_production_subject(
    id, lifecycle, current_order_id, current_order_line_id, item_id, gtin,
    hu_id, hu_code_snapshot, subject_quantity, created_at, completed_at)
VALUES ('97010000-0000-0000-0000-000000000030', 'COMPLETED', NULL, NULL, 9701,
        '04600000009701', (SELECT id FROM hus WHERE hu_code = 'V0040-REAL-HU'),
        'V0040-REAL-HU', 50, '2026-08-25T09:00:00.000Z', '2026-08-25T09:30:00.000Z');
INSERT INTO marking_ready_hu_fact(
    id, marking_subject_id, receipt_doc_id, receipt_line_id, hu_id,
    hu_code_snapshot, item_id_snapshot, gtin_snapshot, marked_quantity,
    provenance, created_at)
VALUES ('97010000-0000-0000-0000-000000000040',
        '97010000-0000-0000-0000-000000000030', 9703, 9703,
        (SELECT id FROM hus WHERE hu_code = 'V0040-REAL-HU'),
        'V0040-REAL-HU', 9701, '04600000009701', 50, 'REAL_IMPORT',
        '2026-08-25T09:30:00.000Z');

INSERT INTO marking_legacy_cutover_cohort(
    id, cutover_state_id, snapshot_schema_version, preflight_hash, snapshot_hash,
    captured_by, captured_at)
VALUES (TRUE, TRUE, 1, 'TEST-V0040-PREFLIGHT', 'TEST-V0040-SNAPSHOT',
        'SERVER:test', '2026-08-25T10:00:00.000Z');
INSERT INTO marking_legacy_cutover_line_scope(
    cohort_id, order_id, order_line_id, order_type, marking_responsibility,
    line_revision, item_id_snapshot, gtin_snapshot, frozen_quantity,
    shipped_quantity_at_cutover, frozen_unshipped_legacy_quantity,
    production_need_snapshot, snapshot_hash, captured_by, captured_at)
VALUES (TRUE, 9701, 9701, 'CUSTOMER', 'FLOWSTOCK', 0, 9701, '04600000009701',
        100, 0, 100, 100, 'TEST-V0040-LINE-SNAPSHOT', 'SERVER:test',
        '2026-08-25T10:00:00.000Z');
UPDATE marking_cutover_state
SET state = 'ENFORCED', preflight_hash = 'TEST-V0040-PREFLIGHT',
    preflight_generated_at = '2026-08-25T10:00:00.000Z',
    preflight_approved_at = '2026-08-25T10:00:00.000Z',
    preflight_approved_by = 'SERVER:test', enforced_at = '2026-08-25T10:00:00.000Z',
    enforced_by = 'SERVER:test', updated_at = '2026-08-25T10:00:00.000Z'
WHERE id = TRUE;
");
    }

    private static void AddV0040MixedPhysicalComposition(NpgsqlConnection connection, string huCode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9703, 'TEST-V0040-MIXED-ITEM', 'TEST-V0040-MIXED-ITEM', '04600000009703', 9701);
INSERT INTO doc_lines(id, doc_id, item_id, qty, to_location_id, to_hu)
VALUES (9704, 9703, 9703, 50, 9701, @hu_code);
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code)
VALUES ('2026-08-25T09:30:00.000Z', 9703, 9703, 9701, 50, @hu_code);";
        command.Parameters.AddWithValue("@hu_code", huCode);
        command.ExecuteNonQuery();
    }

    private static V0040StableSubjectAllocationFixture SeedV0040StableSubjectAllocationScenario(
        NpgsqlConnection connection,
        bool newSubjectSortsBeforeExisting)
    {
        SeedV0040OutboundScenario(connection);
        var existingSubjectId = newSubjectSortsBeforeExisting
            ? Guid.Parse("e0000000-0000-0000-0000-000000000001")
            : Guid.Parse("10000000-0000-0000-0000-000000000001");
        var newSubjectId = newSubjectSortsBeforeExisting
            ? Guid.Parse("00000000-0000-0000-0000-000000000002")
            : Guid.Parse("f0000000-0000-0000-0000-000000000002");

        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO marking_production_subject(
    id, lifecycle, revision,
    original_production_pallet_id, original_component_id,
    original_order_id, original_order_line_id,
    current_production_pallet_id, current_component_id,
    current_order_id, current_order_line_id,
    item_id, gtin, subject_quantity, created_at)
VALUES
(@existing_subject_id, 'ACTIVE', 0,
 9801, 9801, 9701, 9701, NULL, 9801, 9701, 9701,
 9701, '04600000009701', 60, '2026-08-25T09:00:00.000Z'),
(@new_subject_id, 'ACTIVE', 0,
 9802, 9802, 9701, 9701, NULL, 9802, 9701, 9701,
 9701, '04600000009701', 90, '2026-08-25T12:00:00.000Z');

INSERT INTO marking_legacy_cutover_subject_exemption(
    id, frozen_line_scope_id, marking_subject_id, item_id_snapshot,
    gtin_snapshot, subject_revision_snapshot, subject_quantity_snapshot,
    granted_quantity, active_quantity, basis, root_subject_id,
    predecessor_subject_id, allocation_hash, granted_by, granted_at)
SELECT (md5('test-v0040-stable-existing:' || @existing_subject_id::text))::uuid,
       scope.id, @existing_subject_id, 9701, '04600000009701', 0, 60,
       60, 60, 'CUTOVER_EXISTING', @existing_subject_id, NULL,
       md5('test-v0040-stable-existing-allocation:' || @existing_subject_id::text),
       'SERVER:test', '2026-08-25T10:00:00.000Z'
FROM marking_legacy_cutover_line_scope scope
WHERE scope.order_line_id = 9701;

SELECT rebalance_marking_legacy_line_exemptions(
    9701, '2026-08-25T12:00:00.000Z', 'new_subject_planned');";
        command.Parameters.AddWithValue("@existing_subject_id", existingSubjectId);
        command.Parameters.AddWithValue("@new_subject_id", newSubjectId);
        command.ExecuteNonQuery();

        return new V0040StableSubjectAllocationFixture(existingSubjectId, newSubjectId);
    }

    private static void AssertV0040StableSubjectAllocation(
        NpgsqlConnection connection,
        V0040StableSubjectAllocationFixture subjects)
    {
        Assert.Equal(60, ReadV0040SubjectExemption(connection, subjects.ExistingSubjectId));
        Assert.Equal(40, ReadV0040SubjectExemption(connection, subjects.NewSubjectId));
        Assert.Equal(60, ReadV0040SubjectExemptionGrant(connection, subjects.ExistingSubjectId));
        Assert.Equal(40, ReadV0040SubjectExemptionGrant(connection, subjects.NewSubjectId));
        Assert.Equal(50, ExecuteScalarInt(connection, @"
SELECT subject.subject_quantity - exemption.active_quantity
FROM marking_production_subject subject
INNER JOIN marking_legacy_cutover_subject_exemption exemption
        ON exemption.marking_subject_id = subject.id
WHERE subject.id = @new_subject_id;", ("@new_subject_id", subjects.NewSubjectId)));
    }

    private static int ReadV0040SubjectExemption(NpgsqlConnection connection, Guid subjectId)
        => ExecuteScalarInt(connection, @"
SELECT active_quantity
FROM marking_legacy_cutover_subject_exemption
WHERE marking_subject_id = @subject_id;", ("@subject_id", subjectId));

    private static int ReadV0040SubjectExemptionGrant(NpgsqlConnection connection, Guid subjectId)
        => ExecuteScalarInt(connection, @"
SELECT granted_quantity
FROM marking_legacy_cutover_subject_exemption
WHERE marking_subject_id = @subject_id;", ("@subject_id", subjectId));

    private static void AddLegacyProductionDocument(NpgsqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO docs(id, doc_ref, type, status, created_at, order_id, order_ref)
VALUES (@document_id, 'TEST-CUTOVER-HISTORY-PRD-' || @document_id::text,
        'PRODUCTION_RECEIPT', 'DRAFT', '2026-08-24T09:00:00.000Z',
        9401, 'TEST-CUTOVER-HISTORY-ORDER');";
        command.Parameters.AddWithValue("@document_id", documentId);
        command.ExecuteNonQuery();
    }

    private static void AddMarkingPallet(
        NpgsqlConnection connection,
        long palletId,
        string palletStatus,
        double plannedQuantity,
        double filledQuantity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (@pallet_id, 9401, 9401, 9401, @planned_quantity, 9401,
        'TEST-CUTOVER-HISTORY-HU-' || @pallet_id::text);

INSERT INTO production_pallets(
    id, prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
    hu_code, planned_qty, to_location_id, status, created_at)
VALUES (@pallet_id, 9401, @pallet_id, 9401, 9401, 9401,
        'TEST-CUTOVER-HISTORY-HU-' || @pallet_id::text,
        @planned_quantity, 9401, @pallet_status, '2026-08-24T10:00:00.000Z');

INSERT INTO production_pallet_lines(
    id, production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES (@pallet_id, @pallet_id, @pallet_id, 9401, 9401,
        @planned_quantity, @filled_quantity, '2026-08-24T10:00:00.000Z');";
        command.Parameters.AddWithValue("@pallet_id", palletId);
        command.Parameters.AddWithValue("@pallet_status", palletStatus);
        command.Parameters.AddWithValue("@planned_quantity", plannedQuantity);
        command.Parameters.AddWithValue("@filled_quantity", filledQuantity);
        command.ExecuteNonQuery();
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

    private static int ExecuteScalarInt(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
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

    private sealed record V0040StableSubjectAllocationFixture(
        Guid ExistingSubjectId,
        Guid NewSubjectId);

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
