using System.Data;
using System.Globalization;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingHybridCoveragePostgresTests
{
    [Fact]
    public async Task DiagnosticReader_RejectsReadCommittedTransactions()
    {
        await using var connection = await OpenDisposableTestDatabase();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MarkingHybridCoverageReader.ReadTaskAsync(connection, tx, Guid.NewGuid()));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task DiagnosticReader_SeparatesLegacyAndQuantityWithoutClaimingMixedCoverage()
    {
        await using var connection = await OpenDisposableTestDatabase();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var suffix = Guid.NewGuid().ToString("N");
        var gtin = "0460" +
            (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) % 10000000000UL)
            .ToString("D10", CultureInfo.InvariantCulture);

        var type = await Scalar<long>(connection, tx, """
INSERT INTO item_types(name, code, enable_marking)
VALUES (@name, @code, TRUE) RETURNING id;
""", ("name", "TEST-193-TYPE-" + suffix), ("code", "T193-" + suffix));

        var item = await Scalar<long>(connection, tx, """
INSERT INTO items(name, barcode, item_type_id, gtin)
VALUES (@name, @barcode, @type, @gtin) RETURNING id;
""", ("name", "TEST-193-ITEM-" + suffix), ("barcode", suffix),
            ("type", type), ("gtin", gtin));

        var order1 = await CreateOrder(connection, tx, suffix + "-1", item);
        var order2 = await CreateOrder(connection, tx, suffix + "-2", item);
        var task1 = await CreateTask(connection, tx, suffix + "-1", item, gtin,
            order1.OrderId, order1.LineId);
        var task2 = await CreateTask(connection, tx, suffix + "-2", item, gtin,
            order2.OrderId, order2.LineId);
        var task3 = await CreateTask(connection, tx, suffix + "-3", item, gtin,
            null, null);

        var tempImport = Guid.NewGuid();
        var csvImport = Guid.NewGuid();
        var secondTempImport = Guid.NewGuid();
        await InsertImport(connection, tx, suffix + "-temp", task1,
            tempImport, "temporary-chz-export");
        await InsertImport(connection, tx, suffix + "-csv", task1,
            csvImport, "csv");
        await InsertImport(connection, tx, suffix + "-temp2", task2,
            secondTempImport, "temporary-chz-export");

        var doc = await Scalar<long>(connection, tx, """
INSERT INTO docs(doc_ref, type, status, created_at)
VALUES (@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @at) RETURNING id;
""", ("ref", "TEST-193-PRD-" + suffix), ("at", "2026-11-13T12:00:00Z"));
        var line = await Scalar<long>(connection, tx, """
INSERT INTO doc_lines(doc_id, item_id, order_line_id, qty)
VALUES (@doc, @item, @order_line, 100) RETURNING id;
""", ("doc", doc), ("item", item), ("order_line", order1.LineId));

        // The production writer is now fail-closed in both directions:
        // it cannot ISSUE a pool after TEMP-CHZ codes were generated.
        // This test intentionally injects a *historical/ambiguous* mixed
        // snapshot via direct disposable-DB SQL after issuing the pool, so
        // the diagnostic reader still proves that it handles legacy overlap
        // without treating observed totals as authorized coverage.
        var pool1 = Guid.NewGuid();
        await Apply(connection, tx, pool1, "ISSUE", 10, task1, "new-quantity-" + suffix);
        await Apply(connection, tx, pool1, "ASSIGN", 6,
            receiptDoc: doc, receiptLine: line);
        await Apply(connection, tx, pool1, "RELEASE", 2,
            receiptDoc: doc, receiptLine: line);
        await Apply(connection, tx, pool1, "VOID", 1);

        // Group two per-code rows into a single import/status/GTIN scope.
        await InsertCode(connection, tx, suffix + "-1", task1, tempImport,
            gtin, "Reserved", "LegacySynthetic", true);
        await InsertCode(connection, tx, suffix + "-2", task1, tempImport,
            " " + gtin + " ", "Reserved", "LegacySynthetic", true);
        await InsertCode(connection, tx, suffix + "-3", task1, tempImport,
            gtin, "Applied", "LegacySynthetic", true, doc, line);
        await InsertCode(connection, tx, suffix + "-4", task1, tempImport,
            gtin, "Voided", "LegacySynthetic", true);
        await InsertCode(connection, tx, suffix + "-5", task1, tempImport,
            gtin, "Reserved", "HistoricalUnknown", true);
        await InsertCode(connection, tx, suffix + "-6", task1, csvImport,
            gtin, "Reserved", "RealImport", false);
        await InsertCode(connection, tx, suffix + "-7", task1, csvImport,
            gtin, "Quarantined", "RealImport", false);
        await InsertCode(connection, tx, suffix + "-8", task1, csvImport,
            gtin, "Reserved", "HistoricalUnknown", false);
        await InsertCode(connection, tx, suffix + "-9", task1, tempImport,
            gtin, "Reserved", "LegacySynthetic", false);

        // Other order/line, same item + GTIN: must never leak into task1.
        await InsertCode(connection, tx, suffix + "-10", task2, secondTempImport,
            gtin, "Reserved", "LegacySynthetic", true);

        // Separate production-need task has no per-code rows.
        var pool3 = Guid.NewGuid();
        await Apply(connection, tx, pool3, "ISSUE", 3, task3, "new-quantity-need-" + suffix);

        var eventsBefore = await Scalar<long>(connection, tx,
            "SELECT COUNT(*) FROM marking_synthetic_quantity_event;");
        var legacyBefore = await Scalar<long>(connection, tx,
            "SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;",
            ("task", task1));

        var mixed = await MarkingHybridCoverageReader.ReadTaskAsync(
            connection, tx, task1);
        Assert.Equal(9, mixed.PerCodeRows);
        Assert.Equal(10, mixed.QuantityIssued);
        Assert.Equal(7, mixed.PerCodeObservedActive);
        Assert.Equal(9, mixed.QuantityObservedActive);
        Assert.Equal(16, mixed.ObservedPotentialActive);
        Assert.True(mixed.RequiresReconciliation);
        Assert.Null(mixed.UnambiguousObservedActive);
        var mixedDecision = MarkingQuantityEvidencePolicy.Evaluate(mixed);
        Assert.Equal(MarkingQuantityEvidenceState.Blocked, mixedDecision.State);
        Assert.Equal("MIXED_SOURCE", mixedDecision.RefusalCode);

        var tempReserved = Assert.Single(mixed.Rows.Where(row =>
            row.SourceFamily == "PerCode"
            && row.SourceId == tempImport.ToString()
            && row.Status == "Reserved" && row.Origin == "LegacySynthetic"
            && row.Evidence == "ProvenTemporary"));
        Assert.Equal(2, tempReserved.Quantity);
        Assert.Equal(gtin, tempReserved.NormalizedGtin);
        Assert.Equal(order1.LineId, tempReserved.OrderLineId);
        Assert.Equal(order1.OrderId, tempReserved.OrderId);

        Assert.Contains(mixed.Rows, row =>
            row.SourceFamily == "PerCode"
            && row.Origin == "HistoricalUnknown"
            && row.Evidence == "ProvenTemporary");
        Assert.Contains(mixed.Rows, row =>
            row.SourceFamily == "PerCode"
            && row.Origin == "HistoricalUnknown"
            && row.Evidence == "Unverified");
        Assert.Contains(mixed.Rows, row =>
            row.SourceFamily == "PerCode"
            && row.Origin == "RealImport"
            && row.Evidence == "RealCode");
        Assert.Contains(mixed.Rows, row =>
            row.SourceFamily == "PerCode"
            && row.Status == "Quarantined");

        var allocated = Assert.Single(mixed.Rows.Where(row =>
            row.SourceFamily == "QuantityPool"
            && row.SourceId == pool1.ToString()
            && row.Status == "Applied"));
        Assert.Equal(4, allocated.Quantity);
        Assert.Equal(doc, allocated.ReceiptDocId);
        Assert.Equal(line, allocated.ReceiptLineId);
        Assert.Equal(order1.LineId, allocated.OrderLineId);
        Assert.Contains(mixed.Rows, row =>
            row.SourceFamily == "QuantityPool" && row.Status == "Reserved"
            && row.Quantity == 5);
        Assert.Contains(mixed.Rows, row =>
            row.SourceFamily == "QuantityPool" && row.Status == "Voided"
            && row.Quantity == 1);
        Assert.Equal(10, mixed.Rows.Where(row => row.SourceFamily == "QuantityPool")
            .Sum(row => row.Quantity));

        var otherOrder = await MarkingHybridCoverageReader.ReadTaskAsync(
            connection, tx, task2);
        Assert.Equal(1, otherOrder.PerCodeRows);
        Assert.Equal(0, otherOrder.QuantityIssued);
        Assert.False(otherOrder.RequiresReconciliation);
        Assert.Equal(1L, otherOrder.UnambiguousObservedActive);
        Assert.All(otherOrder.Rows, row =>
            Assert.Equal(order2.LineId, row.OrderLineId));
        Assert.Equal(MarkingQuantityEvidenceState.LegacyOnly,
            MarkingQuantityEvidencePolicy.Evaluate(otherOrder).State);

        var onlyQuantity = await MarkingHybridCoverageReader.ReadTaskAsync(
            connection, tx, task3);
        Assert.Equal(0, onlyQuantity.PerCodeRows);
        Assert.Equal(3, onlyQuantity.QuantityIssued);
        Assert.False(onlyQuantity.RequiresReconciliation);
        Assert.Equal(3L, onlyQuantity.UnambiguousObservedActive);
        Assert.All(onlyQuantity.Rows, row => Assert.Null(row.OrderLineId));
        var quantityDecision = MarkingQuantityEvidencePolicy.Evaluate(onlyQuantity);
        Assert.Equal(MarkingQuantityEvidenceState.VerifiedQuantity, quantityDecision.State);
        Assert.Equal(3, quantityDecision.IssuedQuantity);
        Assert.Equal(3, quantityDecision.AvailableQuantity);
        Assert.Equal(0, quantityDecision.AppliedQuantity);
        Assert.Equal(0, quantityDecision.VoidedQuantity);
        Assert.Equal("PRODUCTION_NEED", quantityDecision.SourceType);
        Assert.Equal(item, quantityDecision.ItemId);
        Assert.Equal(gtin, quantityDecision.NormalizedGtin);
        Assert.Empty(quantityDecision.Receipts);

        var unknownTask = await MarkingHybridCoverageReader.ReadTaskAsync(
            connection, tx, Guid.NewGuid());
        Assert.Empty(unknownTask.Rows);
        Assert.Equal(0, unknownTask.ObservedPotentialActive);
        Assert.Equal(MarkingQuantityEvidenceState.NoCoverage,
            MarkingQuantityEvidencePolicy.Evaluate(unknownTask).State);

        // The diagnostic reader is strictly read-only.
        Assert.Equal(eventsBefore, await Scalar<long>(connection, tx,
            "SELECT COUNT(*) FROM marking_synthetic_quantity_event;"));
        Assert.Equal(legacyBefore, await Scalar<long>(connection, tx,
            "SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;",
            ("task", task1)));

        await tx.RollbackAsync();
        Assert.Equal(0, await Scalar<long>(connection, null,
            "SELECT COUNT(*) FROM marking_order WHERE id = @task;",
            ("task", task1)));
    }

    private static async Task<(long OrderId, long LineId)> CreateOrder(
        NpgsqlConnection connection, NpgsqlTransaction tx, string suffix, long item)
    {
        var order = await Scalar<long>(connection, tx, """
INSERT INTO orders(order_ref, order_type, status, created_at)
VALUES (@ref, 'INTERNAL', 'ACCEPTED', @at) RETURNING id;
""", ("ref", "TEST-193-ORDER-" + suffix),
            ("at", "2026-11-13T12:00:00Z"));
        var line = await Scalar<long>(connection, tx, """
INSERT INTO order_lines(order_id, item_id, qty_ordered)
VALUES (@order, @item, 100) RETURNING id;
""", ("order", order), ("item", item));
        return (order, line);
    }

    private static async Task<Guid> CreateTask(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        string suffix, long item, string gtin,
        long? order, long? orderLine)
    {
        var task = Guid.NewGuid();
        await Execute(connection, tx, """
INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin, requested_quantity,
    request_number, status, source_type, source_order_id,
    created_at, updated_at)
VALUES (@id, @order, @line, @item, @gtin, 100,
        @ref, 'WaitingForCodes', @source, @source_order, @at, @at);
""", ("id", task), ("order", order), ("line", orderLine),
            ("item", item), ("gtin", gtin),
            ("ref", "TEST-193-TASK-" + suffix),
            ("source", order.HasValue ? "PRODUCTION_ORDER" : "PRODUCTION_NEED"),
            ("source_order", order), ("at", "2026-11-13T12:00:00Z"));
        return task;
    }

    private static async Task InsertImport(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        string suffix, Guid task, Guid import, string source)
    {
        await Execute(connection, tx, """
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, created_at)
VALUES (@id, @file, @path, @hash, @source, @task, 'Imported', @at);
""", ("id", import), ("file", suffix + ".txt"),
            ("path", source == "temporary-chz-export" ? "<temporary-chz-export>" : "<test>"),
            ("hash", "TEST-193-HASH-" + suffix),
            ("source", source), ("task", task),
            ("at", "2026-11-13T12:00:00Z"));
    }

    private static async Task InsertCode(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        string suffix, Guid task, Guid import, string gtin,
        string status, string origin, bool temporary,
        long? doc = null, long? line = null)
    {
        var code = (temporary ? "TEMP-CHZ-" : "TEST-193-") + suffix;
        await Execute(connection, tx, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, receipt_doc_id, receipt_line_id,
    created_at, updated_at)
VALUES (@id, @code, @hash, @gtin, @task, @import,
        @status, @origin, @doc, @line, @at, @at);
""", ("id", Guid.NewGuid()), ("code", code), ("hash", suffix + "-HASH"),
            ("gtin", gtin), ("task", task), ("import", import),
            ("status", status), ("origin", origin),
            ("doc", doc), ("line", line), ("at", "2026-11-13T12:00:00Z"));
    }

    private static async Task Apply(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid pool, string action, long quantity,
        Guid? task = null, string? source = null,
        long? receiptDoc = null, long? receiptLine = null)
    {
        await Execute(connection, tx, """
SELECT * FROM marking_synthetic_quantity_apply(
    @operation::uuid, @pool::uuid, @action::text, @qty::bigint,
    @task::uuid, @source::text, @doc::bigint, @line::bigint, @actor::text);
""", ("operation", Guid.NewGuid()), ("pool", pool),
            ("action", action), ("qty", quantity), ("task", task),
            ("source", source), ("doc", receiptDoc), ("line", receiptLine),
            ("actor", "test-193"));
    }

    private static async Task<T> Scalar<T>(
        NpgsqlConnection connection, NpgsqlTransaction? tx,
        string sql, params (string Key, object? Value)[] args)
    {
        await using var cmd = Command(connection, tx, sql, args);
        var value = await cmd.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task Execute(
        NpgsqlConnection connection, NpgsqlTransaction? tx,
        string sql, params (string Key, object? Value)[] args)
    {
        await using var cmd = Command(connection, tx, sql, args);
        await cmd.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql,
        params (string Key, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (key, value) in args)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }

    private static async Task<NpgsqlConnection> OpenDisposableTestDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("PostgreSQL test database is required.");

        var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        if (connection.Database != "flowstock_test")
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("Only disposable flowstock_test is permitted.");
        }
        return connection;
    }
}
