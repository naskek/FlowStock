using System.Globalization;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingSyntheticQuantityEventsPostgresTests
{
    [Fact]
    public async Task IssueAssignReleaseVoid_AreIdempotentAtomicAndRecoverable()
    {
        await using var connection = await OpenDisposableTestDatabase();
        await using var tx = await connection.BeginTransactionAsync();
        var fixture = await Fixture.Create(connection, tx);
        var pool = Guid.NewGuid();
        var issue = Guid.NewGuid();

        var state = await Apply(connection, tx, issue, pool, "ISSUE", 12,
            fixture.TaskId, "order-export:revision-1");
        Assert.Equal(new Quantities(12, 12, 0, 0, 1), state);

        // Replaying a committed or in-flight operation cannot add coverage.
        Assert.Equal(state, await Apply(connection, tx, issue, pool, "ISSUE", 12,
            fixture.TaskId, "order-export:revision-1"));
        Assert.Equal(1, await CountEvents(connection, tx, pool));

        await ExpectRejected(connection, tx, () => Apply(connection, tx, issue,
            pool, "ISSUE", 13, fixture.TaskId, "order-export:revision-1"), "23505");
        await ExpectRejected(connection, tx, () => Apply(connection, tx, Guid.NewGuid(),
            pool, "ASSIGN", 1, doc: fixture.DocId + 100000, line: fixture.LineId), "23514");

        state = await Apply(connection, tx, Guid.NewGuid(), pool, "ASSIGN", 7,
            doc: fixture.DocId, line: fixture.LineId);
        Assert.Equal(new Quantities(12, 5, 7, 0, 2), state);

        await ExpectRejected(connection, tx, () => Apply(connection, tx, Guid.NewGuid(),
            pool, "ASSIGN", 6, doc: fixture.DocId, line: fixture.LineId), "23514");
        await ExpectRejected(connection, tx, () => Apply(connection, tx, Guid.NewGuid(),
            pool, "RELEASE", 8, doc: fixture.DocId, line: fixture.LineId), "23514");

        state = await Apply(connection, tx, Guid.NewGuid(), pool, "RELEASE", 3,
            doc: fixture.DocId, line: fixture.LineId);
        Assert.Equal(new Quantities(12, 8, 4, 0, 3), state);

        state = await Apply(connection, tx, Guid.NewGuid(), pool, "VOID", 2);
        Assert.Equal(new Quantities(12, 6, 4, 2, 4), state);
        Assert.Equal(12, state.Available + state.Applied + state.Voided);

        Assert.Equal(4, await Scalar<long>(connection, tx, """
SELECT applied_quantity FROM marking_synthetic_quantity_receipt_balance
WHERE pool_id = @pool AND receipt_doc_id = @doc AND receipt_line_id = @line;
""", ("pool", pool), ("doc", fixture.DocId), ("line", fixture.LineId)));
        Assert.Equal(0, await Scalar<long>(connection, tx, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));
        Assert.Equal(4, await CountEvents(connection, tx, pool));

        // Immutable journal: an accidental edit is rejected, not silently applied.
        await ExpectRejected(connection, tx, () => Execute(connection, tx, """
UPDATE marking_synthetic_quantity_event SET quantity = 100 WHERE pool_id = @pool;
""", ("pool", pool)), "55000");
        await ExpectRejected(connection, tx, () => Execute(connection, tx, """
DELETE FROM marking_synthetic_quantity_pool WHERE id = @pool;
""", ("pool", pool)), "55000");

        // Roll back the entire fixture, journal and allocation. Neither a
        // production document nor a legacy code is touched.
        await tx.RollbackAsync();
        Assert.Equal(0, await Scalar<long>(connection, null, """
SELECT COUNT(*) FROM marking_synthetic_quantity_pool WHERE id = @pool;
""", ("pool", pool)));
        Assert.Equal(0, await Scalar<long>(connection, null, """
SELECT COUNT(*) FROM marking_synthetic_quantity_event WHERE operation_id = @op;
""", ("op", issue)));
    }

    [Fact]
    public async Task ConcurrentAssignments_CannotSpendTheSameAvailableQuantityTwice()
    {
        // A committed fixture must be visible to two separate PostgreSQL
        // sessions. This test is deliberately limited to the disposable CI DB;
        // immutable rows are retained until that test database is destroyed.
        await using var setup = await OpenDisposableTestDatabase();
        var fixture = await Fixture.Create(setup, null);
        var pool = Guid.NewGuid();
        await Apply(setup, null, Guid.NewGuid(), pool, "ISSUE", 10,
            fixture.TaskId, "concurrent-issue");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Compete()
        {
            await gate.Task;
            await using var session = await OpenDisposableTestDatabase();
            try
            {
                await Apply(session, null, Guid.NewGuid(), pool, "ASSIGN", 8,
                    doc: fixture.DocId, line: fixture.LineId);
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == "23514")
            {
                return false;
            }
        }

        var first = Task.Run(Compete);
        var second = Task.Run(Compete);
        gate.SetResult();
        var success = await Task.WhenAll(first, second);
        Assert.Single(success.Where(value => value));

        var result = await ReadBalance(setup, pool);
        Assert.Equal(new Quantities(10, 2, 8, 0, 2), result);
        Assert.Equal(2, await CountEvents(setup, null, pool));

        // A previously committed operation can be replayed without changes.
        // The old per-code table is still completely untouched.
        Assert.Equal(0, await Scalar<long>(setup, null, """
SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @task;
""", ("task", fixture.TaskId)));
    }

    [Fact]
    public async Task InvalidTaskAndReceiptNeverCreateSyntheticCoverage()
    {
        await using var connection = await OpenDisposableTestDatabase();
        await using var tx = await connection.BeginTransactionAsync();
        var fixture = await Fixture.Create(connection, tx);
        var invalidPool = Guid.NewGuid();

        await ExpectRejected(connection, tx, () => Apply(connection, tx, Guid.NewGuid(),
            invalidPool, "ISSUE", 1, Guid.NewGuid(), "missing-task"), "23514");
        Assert.Equal(0, await Scalar<long>(connection, tx, """
SELECT COUNT(*) FROM marking_synthetic_quantity_pool WHERE id = @pool;
""", ("pool", invalidPool)));

        var pool = Guid.NewGuid();
        await Apply(connection, tx, Guid.NewGuid(), pool, "ISSUE", 3,
            fixture.TaskId, "receipt-gate");

        await Execute(connection, tx, """
UPDATE docs SET status = 'CLOSED' WHERE id = @doc;
""", ("doc", fixture.DocId));
        await ExpectRejected(connection, tx, () => Apply(connection, tx,
            Guid.NewGuid(), pool, "ASSIGN", 1,
            doc: fixture.DocId, line: fixture.LineId), "23514");
        Assert.Equal(new Quantities(3, 3, 0, 0, 1),
            await ReadBalance(connection, pool, tx));

        await tx.RollbackAsync();
    }

    private static async Task ExpectRejected(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        Func<Task> operation, string expectedSqlState)
    {
        await Execute(connection, tx, "SAVEPOINT check_rejected;");
        var error = await Assert.ThrowsAsync<PostgresException>(operation);
        Assert.Equal(expectedSqlState, error.SqlState);
        await Execute(connection, tx, "ROLLBACK TO SAVEPOINT check_rejected;");
        await Execute(connection, tx, "RELEASE SAVEPOINT check_rejected;");
    }

    private static async Task<Quantities> Apply(
        NpgsqlConnection connection, NpgsqlTransaction? tx,
        Guid operation, Guid pool, string action, long quantity,
        Guid? task = null, string? source = null, long? doc = null,
        long? line = null)
    {
        await using var command = Command(connection, tx, """
SELECT issued_quantity, available_quantity, applied_quantity, voided_quantity, revision
FROM marking_synthetic_quantity_apply(
    @op::uuid, @pool::uuid, @action::text, @qty::bigint,
    @task::uuid, @source::text, @doc::bigint, @line::bigint, @actor::text);
""",
            ("op", operation), ("pool", pool), ("action", action),
            ("qty", quantity), ("task", task), ("source", source),
            ("doc", doc), ("line", line), ("actor", "postgres-regression"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new Quantities(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task<Quantities> ReadBalance(
        NpgsqlConnection connection, Guid pool, NpgsqlTransaction? tx = null)
    {
        await using var command = Command(connection, tx, """
SELECT issued_quantity, available_quantity, applied_quantity,
       voided_quantity, revision
FROM marking_synthetic_quantity_balance WHERE pool_id = @pool;
""", ("pool", pool));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Quantities(reader.GetInt64(0), reader.GetInt64(1),
            reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static async Task<long> CountEvents(
        NpgsqlConnection connection, NpgsqlTransaction? tx, Guid pool) =>
        await Scalar<long>(connection, tx, """
SELECT COUNT(*) FROM marking_synthetic_quantity_event WHERE pool_id = @pool;
""", ("pool", pool));

    private static async Task<T> Scalar<T>(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql,
        params (string Key, object? Value)[] args)
    {
        await using var command = Command(connection, tx, sql, args);
        var result = await command.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task Execute(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql,
        params (string Key, object? Value)[] args)
    {
        await using var command = Command(connection, tx, sql, args);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql,
        params (string Key, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (key, value) in args)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    private static async Task<NpgsqlConnection> OpenDisposableTestDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required.");

        var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        // Concurrency fixtures are committed and intentionally not deleted
        // (append-only history). Never run this suite on a production database.
        if (connection.Database != "flowstock_test")
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                "Synthetic quantity integration tests require disposable flowstock_test database.");
        }
        return connection;
    }

    private sealed record Quantities(
        long Issued, long Available, long Applied, long Voided, long Revision);

    private sealed record Fixture(Guid TaskId, long DocId, long LineId)
    {
        public static async Task<Fixture> Create(
            NpgsqlConnection connection, NpgsqlTransaction? tx)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var typeId = await Scalar<long>(connection, tx, """
INSERT INTO item_types(name, code, enable_marking)
VALUES (@name, @code, TRUE) RETURNING id;
""", ("name", "TEST-191-TYPE-" + suffix), ("code", "T191-" + suffix));

            var itemId = await Scalar<long>(connection, tx, """
INSERT INTO items(name, barcode, gtin, item_type_id)
VALUES (@name, @barcode, '04601234567890', @type) RETURNING id;
""", ("name", "TEST-191-ITEM-" + suffix),
                ("barcode", "TEST-191-BARCODE-" + suffix), ("type", typeId));

            var task = Guid.NewGuid();
            await Execute(connection, tx, """
INSERT INTO marking_order(
    id, item_id, gtin, requested_quantity, request_number, status,
    source_type, created_at, updated_at)
VALUES (@id, @item, '04601234567890', 100,
        @ref, 'WaitingForCodes', 'PRODUCTION_NEED', @at, @at);
""", ("id", task), ("item", itemId),
                ("ref", "TEST-191-TASK-" + suffix),
                ("at", "2026-11-13T12:00:00.000Z"));

            var doc = await Scalar<long>(connection, tx, """
INSERT INTO docs(doc_ref, type, status, created_at)
VALUES (@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @at) RETURNING id;
""", ("ref", "TEST-191-PRD-" + suffix),
                ("at", "2026-11-13T12:00:00.000Z"));

            var line = await Scalar<long>(connection, tx, """
INSERT INTO doc_lines(doc_id, item_id, qty)
VALUES (@doc, @item, 100) RETURNING id;
""", ("doc", doc), ("item", itemId));

            return new Fixture(task, doc, line);
        }
    }
}
