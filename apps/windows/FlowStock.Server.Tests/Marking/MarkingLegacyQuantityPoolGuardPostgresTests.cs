using System.Globalization;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingLegacyQuantityPoolGuardPostgresTests
{
    [Fact]
    public async Task LegacyExcelExport_RetainsNormalGenerationAndRetryWhenNoPoolExists()
    {
        var cs = RequiredDisposablePostgresConnection();
        var fixture = await CreateFixture(cs);
        var store = new PostgresDataStore(cs);

        var result = new MarkingExcelService(store).Export(
            new[] { fixture.TaskId }, Array.Empty<long>(), DateTime.UtcNow);
        Assert.True(result.IsSuccess);

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        Assert.Equal(3L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(0L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_synthetic_quantity_pool WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));

        var replay = new MarkingExcelService(store).Export(
            new[] { fixture.TaskId }, Array.Empty<long>(), DateTime.UtcNow);
        Assert.True(replay.IsSuccess);
        Assert.Equal(3L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(1L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code_import WHERE matched_marking_order_id = @task;
""", ("task", fixture.TaskId)));
    }

    [Fact]
    public async Task ExistingPool_BlocksExcelAndDirectTechnicalWriterButNotRealCodeImport()
    {
        var cs = RequiredDisposablePostgresConnection();
        var fixture = await CreateFixture(cs);
        var pool = await IssuePool(cs, fixture.TaskId, quantity: 3);
        var store = new PostgresDataStore(cs);

        // Default Excel path must fail before any import or legacy code is
        // persisted; the full outer transaction also rolls back its statuses.
        var error = Assert.Throws<InvalidOperationException>(() =>
            new MarkingExcelService(store).Export(
                new[] { fixture.TaskId }, Array.Empty<long>(), DateTime.UtcNow));
        Assert.Contains("QUANTITY_POOL_LEGACY_EXPORT_CONFLICT", error.Message);

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        Assert.Equal("WaitingForCodes", await Scalar<string>(connection, """
SELECT status FROM marking_order WHERE id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(0L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code_import WHERE matched_marking_order_id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(0L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(3L, await Scalar<long>(connection, """
SELECT available_quantity FROM marking_synthetic_quantity_balance WHERE pool_id = @pool;
""", ("pool", pool)));

        var tempImportId = Guid.NewGuid();
        store.AddMarkingCodeImport(CreateImport(tempImportId, fixture.TaskId, "temporary-chz-export"));
        var technical = CreateCode(
            fixture.TaskId, tempImportId, "TEMP-CHZ-" + fixture.TaskId + "-000001",
            fixture.Gtin, MarkingCodeOrigin.LegacySynthetic);
        error = Assert.Throws<InvalidOperationException>(() => store.AddMarkingCodes(new[] { technical }));
        Assert.Contains("QUANTITY_POOL_LEGACY_EXPORT_CONFLICT", error.Message);

        Assert.Equal(0L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));

        // Real DataMatrix import does not use the legacy synthetic writer.
        var realImportId = Guid.NewGuid();
        store.AddMarkingCodeImport(CreateImport(realImportId, fixture.TaskId, "csv"));
        var real = CreateCode(fixture.TaskId, realImportId,
            "010460123456789021TEST-195-" + Guid.NewGuid().ToString("N"),
            fixture.Gtin, MarkingCodeOrigin.RealImport);
        store.AddMarkingCodes(new[] { real });
        Assert.Equal(1L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));

        // Once a pool exists, even fully VOIDed provenance must not silently
        // permit per-code regeneration in the same task.
        await Apply(cs, pool, "VOID", 3, null, null);
        error = Assert.Throws<InvalidOperationException>(() => store.MarkMarkingOrdersPrinted(
            new[] { fixture.TaskId }, DateTime.UtcNow));
        Assert.Contains("QUANTITY_POOL_LEGACY_EXPORT_CONFLICT", error.Message);
    }

    [Fact]
    public async Task IncompatibleMarkingTask_RollsBackEarlierOrderStatusInSameTransaction()
    {
        var cs = RequiredDisposablePostgresConnection();
        var fixture = await CreateFixture(cs, withOrder: true);
        await IssuePool(cs, fixture.TaskId, quantity: 3);
        var store = new PostgresDataStore(cs);
        Assert.True(fixture.OrderId.HasValue);

        var error = Assert.Throws<InvalidOperationException>(() =>
            store.ExecuteInTransaction(scoped =>
            {
                scoped.MarkOrdersPrinted(new[] { fixture.OrderId!.Value }, DateTime.UtcNow);
                scoped.MarkMarkingOrdersPrinted(new[] { fixture.TaskId }, DateTime.UtcNow);
            }));
        Assert.Contains("QUANTITY_POOL_LEGACY_EXPORT_CONFLICT", error.Message);

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        Assert.Equal("NOT_REQUIRED", await Scalar<string>(connection, """
SELECT marking_status FROM orders WHERE id = @order;
""", ("order", fixture.OrderId!.Value)));
        Assert.Equal("WaitingForCodes", await Scalar<string>(connection, """
SELECT status FROM marking_order WHERE id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(0L, await Scalar<long>(connection, """
SELECT COUNT(*) FROM marking_code_import WHERE matched_marking_order_id = @task;
""", ("task", fixture.TaskId)));
    }

    private static MarkingCodeImport CreateImport(Guid id, Guid task, string source) =>
        new()
        {
            Id = id,
            OriginalFilename = $"TEST-195-{id}.txt",
            StoragePath = "<regression>",
            FileHash = $"TEST-195-HASH-{id:N}",
            SourceType = source,
            MatchedMarkingOrderId = task,
            Status = MarkingCodeImportStatus.Imported,
            CreatedAt = DateTime.UtcNow
        };

    private static MarkingCode CreateCode(
        Guid task, Guid import, string code, string gtin, string origin) =>
        new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            CodeHash = "TEST-195-" + Guid.NewGuid().ToString("N"),
            Gtin = gtin,
            MarkingOrderId = task,
            ImportId = import,
            Status = MarkingCodeStatus.Reserved,
            Origin = origin,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static async Task<Guid> IssuePool(string cs, Guid task, int quantity)
    {
        var pool = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT issued_quantity FROM marking_synthetic_quantity_apply(
    @op, @pool, 'ISSUE', @quantity,
    @task, @source, NULL::bigint, NULL::bigint, 'test-195');
""";
        command.Parameters.AddWithValue("op", Guid.NewGuid());
        command.Parameters.AddWithValue("pool", pool);
        command.Parameters.AddWithValue("quantity", (long)quantity);
        command.Parameters.AddWithValue("task", task);
        command.Parameters.AddWithValue("source", "test-195-" + pool.ToString("N"));
        Assert.Equal((long)quantity, Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        return pool;
    }

    private static async Task Apply(
        string cs, Guid pool, string action, long quantity, long? doc, long? line)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT issued_quantity FROM marking_synthetic_quantity_apply(
    @op, @pool, @action, @quantity,
    NULL::uuid, NULL::text, @doc::bigint, @line::bigint, 'test-195');
""";
        command.Parameters.AddWithValue("op", Guid.NewGuid());
        command.Parameters.AddWithValue("pool", pool);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("doc", (object?)doc ?? DBNull.Value);
        command.Parameters.AddWithValue("line", (object?)line ?? DBNull.Value);
        await command.ExecuteScalarAsync();
    }

    private sealed record Fixture(Guid TaskId, string Gtin, long? OrderId);

    private static async Task<Fixture> CreateFixture(string cs, bool withOrder = false)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var gtin = "0460" +
            (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) % 10000000000UL)
            .ToString("D10", CultureInfo.InvariantCulture);

        var type = await Scalar<long>(connection, """
INSERT INTO item_types(name, code, enable_marking)
VALUES (@name, @code, TRUE) RETURNING id;
""", ("name", "TEST-195-TYPE-" + suffix), ("code", "T195-" + suffix));

        var item = await Scalar<long>(connection, """
INSERT INTO items(name, barcode, gtin, item_type_id)
VALUES (@name, @barcode, @gtin, @type) RETURNING id;
""", ("name", "TEST-195-ITEM-" + suffix), ("barcode", suffix),
            ("gtin", gtin), ("type", type));

        long? orderId = null;
        if (withOrder)
        {
            orderId = await Scalar<long>(connection, """
INSERT INTO orders(order_ref, order_type, status, created_at)
VALUES (@ref, 'INTERNAL', 'ACCEPTED', @at) RETURNING id;
""", ("ref", "TEST-195-ORDER-" + suffix),
                ("at", "2026-11-13T12:00:00Z"));
        }

        var task = Guid.NewGuid();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
INSERT INTO marking_order(
    id, order_id, item_id, gtin, requested_quantity, request_number,
    status, source_type, source_order_id, created_at, updated_at)
VALUES (@id, @order, @item, @gtin, 3, @ref,
        'WaitingForCodes', @source, @source_order, @at, @at);
""";
        cmd.Parameters.AddWithValue("id", task);
        cmd.Parameters.AddWithValue("order", (object?)orderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("item", item);
        cmd.Parameters.AddWithValue("gtin", gtin);
        cmd.Parameters.AddWithValue("ref", "TEST-195-TASK-" + suffix);
        cmd.Parameters.AddWithValue("source", withOrder ? "PRODUCTION_ORDER" : "PRODUCTION_NEED");
        cmd.Parameters.AddWithValue("source_order", (object?)orderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", "2026-11-13T12:00:00Z");
        await cmd.ExecuteNonQueryAsync();

        // Committed fixture remains in CI's disposable flowstock_test only.
        // Quantity history is immutable and must not be cleaned up by DELETE.
        return new Fixture(task, gtin, orderId);
    }

    private static string RequiredDisposablePostgresConnection()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required.");

        var db = new NpgsqlConnectionStringBuilder(cs).Database;
        if (!string.Equals(db, "flowstock_test", StringComparison.Ordinal))
            throw new InvalidOperationException("Legacy quantity guard tests require disposable flowstock_test.");
        return cs;
    }

    private static async Task<T> Scalar<T>(
        NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        var result = await command.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
