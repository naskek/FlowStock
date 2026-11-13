using System.Globalization;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

// Issue #188: real PostgreSQL contract test. Transactions roll back their fixtures;
// no existing marking_code rows are modified.
public sealed class MarkingSyntheticCoverageShadowPostgresTests
{
    [Fact]
    public async Task ShadowView_GroupsProvenTemporaryCodesAndPreservesOriginStatusReceiptAndImport()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(cs), "PostgreSQL test connection is required.");

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var taskId = Guid.NewGuid();
        var temporaryImportId = Guid.NewGuid();
        var secondTemporaryImportId = Guid.NewGuid();
        var realImportId = Guid.NewGuid();
        var prefix = $"TEST-188-{Guid.NewGuid():N}";
        const string timestamp = "2026-11-13T12:00:00.000Z";

        // A task without an order is valid for PRODUCTION_NEED.
        await Exec(connection, transaction, """
INSERT INTO marking_order(
    id, requested_quantity, request_number, status, source_type, created_at, updated_at)
VALUES (@task, 20, @request, 'Printed', 'PRODUCTION_NEED', @at, @at);
""",
            ("task", taskId), ("request", prefix), ("at", timestamp));

        await Exec(connection, transaction, """
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, created_at)
VALUES
    (@temp, 'synthetic.xlsx', '<temporary-chz-export>', @hash1,
     'temporary-chz-export', @task, 'Imported', @at),
    (@temp2, 'synthetic-2.xlsx', '<temporary-chz-export>', @hash2,
     'temporary-chz-export', @task, 'Imported', @at),
    (@real, 'real.csv', '<memory>', @hash3,
     'csv', @task, 'Imported', @at);
""",
            ("temp", temporaryImportId),
            ("temp2", secondTemporaryImportId),
            ("real", realImportId),
            ("hash1", prefix + "-import-1"),
            ("hash2", prefix + "-import-2"),
            ("hash3", prefix + "-real"),
            ("task", taskId), ("at", timestamp));

        // Valid receipt doc/line, without changing ledger or closed documents.
        var itemId = await Scalar<long>(connection, transaction, """
INSERT INTO items(name, barcode, gtin)
VALUES (@name, @barcode, NULL)
RETURNING id;
""", ("name", prefix), ("barcode", prefix + "-barcode"));

        var docId = await Scalar<long>(connection, transaction, """
INSERT INTO docs(doc_ref, type, status, created_at)
VALUES (@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @at)
RETURNING id;
""", ("ref", prefix), ("at", timestamp));

        var lineId = await Scalar<long>(connection, transaction, """
INSERT INTO doc_lines(doc_id, item_id, qty)
VALUES (@doc, @item, 1)
RETURNING id;
""", ("doc", docId), ("item", itemId));

        // Same import, same status and normalized GTIN: one quantity=2 bucket.
        await Exec(connection, transaction, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id, status, origin,
    created_at, updated_at)
SELECT gen_random_uuid(), @prefix || '-R-' || n,
       @prefix || '-RH-' || n,
       CASE WHEN n = 1 THEN ' 04601234567890 ' ELSE '04601234567890' END,
       @task, @temp, 'Reserved', 'LegacySynthetic', @at, @at
FROM generate_series(1, 2) AS n;
""",
            ("prefix", "TEMP-CHZ-" + prefix), ("task", taskId),
            ("temp", temporaryImportId), ("at", timestamp));

        // Bound applied quantity stays separate from the free pool.
        await Exec(connection, transaction, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, receipt_doc_id, receipt_line_id, applied_at, created_at, updated_at)
VALUES (gen_random_uuid(), @code, @hash, '04601234567890',
        @task, @temp, 'Applied', 'LegacySynthetic',
        @doc, @line, @at, @at, @at);
""",
            ("code", "TEMP-CHZ-" + prefix + "-A"), ("hash", prefix + "-AH"),
            ("task", taskId), ("temp", temporaryImportId),
            ("doc", docId), ("line", lineId), ("at", timestamp));

        // Voided remains explicitly distinguishable and is not active coverage.
        await Exec(connection, transaction, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, created_at, updated_at)
VALUES (gen_random_uuid(), @code, @hash, '04601234567890',
        @task, @temp, 'Voided', 'LegacySynthetic', @at, @at);
""",
            ("code", "TEMP-CHZ-" + prefix + "-V"), ("hash", prefix + "-VH"),
            ("task", taskId), ("temp", temporaryImportId), ("at", timestamp));

        // Proven HistoricalUnknown is included without rewriting its origin.
        await Exec(connection, transaction, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, created_at, updated_at)
VALUES (gen_random_uuid(), @code, @hash, NULL,
        @task, @temp, 'Reserved', 'HistoricalUnknown', @at, @at);
""",
            ("code", "TEMP-CHZ-" + prefix + "-H"), ("hash", prefix + "-HH"),
            ("task", taskId), ("temp", temporaryImportId), ("at", timestamp));

        // A second import must never be merged into the first import's bucket.
        await Exec(connection, transaction, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, created_at, updated_at)
VALUES (gen_random_uuid(), @code, @hash, '04601234567890',
        @task, @temp, 'Reserved', 'LegacySynthetic', @at, @at);
""",
            ("code", "TEMP-CHZ-" + prefix + "-I2"), ("hash", prefix + "-I2H"),
            ("task", taskId), ("temp", secondTemporaryImportId), ("at", timestamp));

        // Three lookalikes must NOT enter the synthetic projection:
        // real import with TEMP prefix, unknown real-like import and non-TEMP prefix.
        await Exec(connection, transaction, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, created_at, updated_at)
VALUES
    (gen_random_uuid(), @real_temp, @real_temp_hash, '04601234567890',
     @task, @real, 'Reserved', 'RealImport', @at, @at),
    (gen_random_uuid(), @unknown, @unknown_hash, '04601234567890',
     @task, @real, 'Reserved', 'HistoricalUnknown', @at, @at),
    (gen_random_uuid(), @non_temp, @non_temp_hash, '04601234567890',
     @task, @temp, 'Reserved', 'LegacySynthetic', @at, @at);
""",
            ("real_temp", "TEMP-CHZ-" + prefix + "-REAL"),
            ("real_temp_hash", prefix + "-REAL-H"),
            ("unknown", prefix + "-UNKNOWN"),
            ("unknown_hash", prefix + "-UNKNOWN-H"),
            ("non_temp", prefix + "-NOT-TEMP"),
            ("non_temp_hash", prefix + "-NOT-TEMP-H"),
            ("task", taskId), ("real", realImportId),
            ("temp", temporaryImportId), ("at", timestamp));

        Assert.Equal(6L, await Scalar<long>(connection, transaction, """
SELECT COALESCE(SUM(quantity), 0)::bigint
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task;
""", ("task", taskId)));

        Assert.Equal(5L, await Scalar<long>(connection, transaction, """
SELECT COUNT(*)::bigint
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task;
""", ("task", taskId)));

        Assert.Equal(2L, await Scalar<long>(connection, transaction, """
SELECT quantity
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task AND import_id = @temp
  AND status = 'Reserved' AND origin = 'LegacySynthetic'
  AND normalized_gtin = '04601234567890' AND receipt_line_id IS NULL;
""", ("task", taskId), ("temp", temporaryImportId)));

        Assert.Equal(1L, await Scalar<long>(connection, transaction, """
SELECT quantity
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task AND status = 'Applied'
  AND receipt_doc_id = @doc AND receipt_line_id = @line;
""", ("task", taskId), ("doc", docId), ("line", lineId)));

        Assert.Equal(1L, await Scalar<long>(connection, transaction, """
SELECT quantity
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task AND origin = 'HistoricalUnknown';
""", ("task", taskId)));

        Assert.Equal(1L, await Scalar<long>(connection, transaction, """
SELECT quantity
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task AND status = 'Voided';
""", ("task", taskId)));

        Assert.Equal(5L, await Scalar<long>(connection, transaction, """
SELECT COALESCE(SUM(quantity) FILTER (WHERE status <> 'Voided'), 0)::bigint
FROM marking_synthetic_coverage_shadow
WHERE marking_order_id = @task;
""", ("task", taskId)));

        Assert.Equal(6L, await Scalar<long>(connection, transaction, """
SELECT COUNT(*)::bigint
FROM marking_code c
JOIN marking_code_import i ON i.id = c.import_id
WHERE c.marking_order_id = @task
  AND c.code LIKE 'TEMP-CHZ-%'
  AND i.source_type = 'temporary-chz-export'
  AND c.origin IN ('LegacySynthetic', 'HistoricalUnknown');
""", ("task", taskId)));

        // Read-only projection cannot become an accidental write entrypoint.
        Assert.Equal(0, await Scalar<int>(connection, transaction, """
SELECT pg_relation_is_updatable('marking_synthetic_coverage_shadow'::regclass, FALSE);
"""));

        await transaction.RollbackAsync();
    }

    private static async Task Exec(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }
}
