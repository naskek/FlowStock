using System.Data;
using System.Globalization;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingQuantityPrdAssignmentPostgresTests
{
    [Fact]
    public async Task SeveralLinesAndPools_UseExactScopeAndPriority_AndReplayWithoutEvents()
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var f = await Seed(db, tx, 6, 4);
        var general = await Pool(db, tx, f, "PRODUCTION_NEED", null, null, 8);
        var otherLine = await Scalar<long>(db, tx, """
INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,6) RETURNING id;
""", ("o", f.Order), ("i", f.Item));
        var line2 = await ReceiptLine(db, tx, f.Doc, f.Item, otherLine, 6);
        var thirdLine = await Scalar<long>(db, tx, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,6) RETURNING id;", ("o", f.Order), ("i", f.Item));
        var wrongLine = await Pool(db, tx, f, "PRODUCTION_ORDER", f.Order, thirdLine, 100);
        var foreignOrder = await NewOrder(db, tx);
        var foreign = await Pool(db, tx, f, "PRODUCTION_ORDER", foreignOrder, null, 100);

        Assert.Equal(12, await Assign(db, tx, f.Doc));
        Assert.Equal(4, await Applied(db, tx, f.Pool, f.Doc, f.Line));
        Assert.Equal(2, await Applied(db, tx, general, f.Doc, f.Line));
        Assert.Equal(6, await Applied(db, tx, general, f.Doc, line2));
        Assert.Equal(0, await Applied(db, tx, wrongLine, f.Doc, line2));
        Assert.Equal(0, await Applied(db, tx, foreign, f.Doc, f.Line));
        var events = await EventCount(db, tx, f.Doc);
        Assert.Equal(12, await Assign(db, tx, f.Doc));
        Assert.Equal(events, await EventCount(db, tx, f.Doc));
        Assert.Equal(0, await Scalar<long>(db, tx, "SELECT COUNT(*) FROM marking_code WHERE marking_order_id=@t;", ("t", f.Task)));
        Assert.Equal("DRAFT", await Scalar<string>(db, tx, "SELECT status FROM docs WHERE id=@d;", ("d", f.Doc)));
        Assert.Equal(0, await Scalar<long>(db, tx, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d;", ("d", f.Doc)));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task LateLineShortage_RollsBackEarlierAssignments_EvenWhenCallerCatchesError()
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var f = await Seed(db, tx, 6, 10);
        await ReceiptLine(db, tx, f.Doc, f.Item, f.OrderLine, 6);
        await Rejected(db, tx, () => Assign(db, tx, f.Doc), "QUANTITY_PRD_SHORTAGE");
        Assert.Equal(0, await EventCount(db, tx, f.Doc));
        Assert.Equal(10, await Free(db, tx, f.Pool));
        Assert.Equal("DRAFT", await Scalar<string>(db, tx, "SELECT status FROM docs WHERE id=@d;", ("d", f.Doc)));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CallerFailureAfterAssignment_RollsBackCoverageAndDocumentTogether()
    {
        await using var db = await Open();
        var f = await Seed(db, null, 10, 10);
        await using (var tx = await db.BeginTransactionAsync())
        {
            Assert.Equal(10, await Assign(db, tx, f.Doc));
            await Execute(db, tx, "UPDATE docs SET status='CLOSED' WHERE id=@d;", ("d", f.Doc));
            await Assert.ThrowsAsync<PostgresException>(() => Execute(db, tx, "SELECT 1/0;"));
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await EventCount(db, null, f.Doc));
        Assert.Equal(10, await Free(db, null, f.Pool));
        Assert.Equal("DRAFT", await Scalar<string>(db, null, "SELECT status FROM docs WHERE id=@d;", ("d", f.Doc)));
        Assert.Equal(10, await Assign(db, null, f.Doc));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("cancelled-order")]
    [InlineData("cancelled-task")]
    [InlineData("fractional")]
    [InlineData("superseded")]
    [InlineData("disabled-marking")]
    [InlineData("gtin-drift")]
    [InlineData("foreign-line")]
    [InlineData("mixed-voided-real")]
    public async Task InvalidLiveScope_FailsClosedWithoutPartialCoverage(string fault)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var f = await Seed(db, tx, 10, 10);
        switch (fault)
        {
            case "closed":
                await Execute(db, tx, "UPDATE docs SET status='CLOSED' WHERE id=@d;", ("d", f.Doc));
                break;
            case "cancelled-order":
                await Execute(db, tx, "UPDATE orders SET status='CANCELLED' WHERE id=@o;", ("o", f.Order));
                break;
            case "cancelled-task":
                await Execute(db, tx, "UPDATE marking_order SET status='Cancelled' WHERE id=@t;", ("t", f.Task));
                break;
            case "fractional":
                await Execute(db, tx, "UPDATE doc_lines SET qty=9.5 WHERE id=@l;", ("l", f.Line));
                break;
            case "superseded":
                await Execute(db, tx, "INSERT INTO doc_lines(doc_id,item_id,qty,replaces_line_id) VALUES (@d,@i,0,@l);", ("d", f.Doc), ("i", f.Item), ("l", f.Line));
                break;
            case "disabled-marking":
                await Execute(db, tx, "UPDATE item_types SET enable_marking=FALSE WHERE id=(SELECT item_type_id FROM items WHERE id=@i);", ("i", f.Item));
                break;
            case "gtin-drift":
                await Execute(db, tx, "UPDATE items SET gtin='99999999999999' WHERE id=@i;", ("i", f.Item));
                break;
            case "foreign-line":
                await Execute(db, tx, "UPDATE order_lines SET order_id=@o WHERE id=@l;", ("o", await NewOrder(db, tx)), ("l", f.OrderLine));
                break;
            case "mixed-voided-real":
                var import = Guid.NewGuid();
                await Execute(db, tx, """
INSERT INTO marking_code_import(id,original_filename,storage_path,file_hash,source_type,matched_marking_order_id,status,created_at)
VALUES (@id,'test.csv','<test>',@raw,'csv',@t,'Imported',@at);
INSERT INTO marking_code(id,code,code_hash,gtin,marking_order_id,import_id,status,created_at,updated_at,origin)
VALUES (@id,@raw,@raw,@g,@t,@id,'Voided',@at,@at,'RealImport');
""", ("id", import), ("t", f.Task), ("g", f.Gtin), ("raw", "REAL-" + import), ("at", At));
                break;
        }
        await Rejected(db, tx, () => Assign(db, tx, f.Doc), "QUANTITY_PRD_");
        Assert.Equal(0, await EventCount(db, tx, f.Doc));
        Assert.Equal(10, await Free(db, tx, f.Pool));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task ChangedReceiptOrReleasedHistory_CannotSilentlyReassign()
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var f = await Seed(db, tx, 10, 20);
        await Assign(db, tx, f.Doc);
        await Execute(db, tx, "UPDATE doc_lines SET qty=11 WHERE id=@l;", ("l", f.Line));
        await Rejected(db, tx, () => Assign(db, tx, f.Doc), "QUANTITY_PRD_HISTORY_CONFLICT");
        await Execute(db, tx, "UPDATE doc_lines SET qty=10 WHERE id=@l;", ("l", f.Line));
        await Execute(db, tx, """
SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'RELEASE',10,NULL,NULL,@d,@l,'test');
""", ("op", Guid.NewGuid()), ("p", f.Pool), ("d", f.Doc), ("l", f.Line));
        await Rejected(db, tx, () => Assign(db, tx, f.Doc), "QUANTITY_PRD_HISTORY_CONFLICT");
        Assert.Equal(20, await Free(db, tx, f.Pool));
        Assert.Equal(2, await EventCount(db, tx, f.Doc));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task RepeatableReadIsRejected_InsteadOfUsingAStalePoolSnapshot()
    {
        await using var db = await Open();
        var f = await Seed(db, null, 10, 10);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await Execute(db, tx, "SAVEPOINT rejected;");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Assign(db, tx, f.Doc));
        Assert.Equal("25000", ex.SqlState);
        await tx.RollbackAsync();
        Assert.Equal(10, await Free(db, null, f.Pool));
    }

    [Fact]
    public async Task ClosedReplayAndPublicExecution_AreNotAuthorized()
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var f = await Seed(db, tx, 10, 10);
        await Assign(db, tx, f.Doc);
        await Execute(db, tx, "UPDATE docs SET status='CLOSED' WHERE id=@d;", ("d", f.Doc));
        await Rejected(db, tx, () => Assign(db, tx, f.Doc), "QUANTITY_PRD_DRAFT_REQUIRED");
        Assert.Equal(1, await EventCount(db, tx, f.Doc));
        Assert.False(await Scalar<bool>(db, tx, """
SELECT EXISTS(SELECT 1 FROM pg_proc p,
    LATERAL aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) a
WHERE p.oid='marking_synthetic_quantity_assign_prd(bigint,text)'::regprocedure
  AND a.grantee=0 AND a.privilege_type='EXECUTE');
"""));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task SupersessionInsert_WaitsUntilAssignmentTransactionFinishes()
    {
        await using var setup = await Open();
        var f = await Seed(setup, null, 10, 10);
        await using var first = await Open();
        await using var tx = await first.BeginTransactionAsync();
        await Assign(first, tx, f.Doc);
        await using var editor = await Open();
        var pending = Execute(editor, null, "INSERT INTO doc_lines(doc_id,item_id,qty,replaces_line_id) VALUES (@d,@i,0,@l);", ("d", f.Doc), ("i", f.Item), ("l", f.Line));
        await WaitForLock(setup, editor.ProcessID);
        await tx.RollbackAsync();
        await pending;
        Assert.Equal(0, await EventCount(setup, null, f.Doc));
        Assert.Equal(10, await Free(setup, null, f.Pool));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReceiptsOrSameReceipt_RecheckAfterActualLockWait(bool sameReceipt)
    {
        await using var setup = await Open();
        var f = await Seed(setup, null, 10, 10);
        var secondDoc = sameReceipt ? f.Doc : await Receipt(setup, null, f.Order);
        if (!sameReceipt) await ReceiptLine(setup, null, secondDoc, f.Item, f.OrderLine, 10);
        await using var first = await Open();
        await using var firstTx = await first.BeginTransactionAsync();
        Assert.Equal(10, await Assign(first, firstTx, f.Doc));

        await using var second = await Open();
        var pending = Assign(second, null, secondDoc);
        await WaitForLock(setup, second.ProcessID);
        await firstTx.CommitAsync();
        if (sameReceipt) Assert.Equal(10, await pending);
        else
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Contains("QUANTITY_PRD_SHORTAGE", ex.MessageText);
        }
        Assert.Equal(0, await Free(setup, null, f.Pool));
        Assert.Equal(1, await EventCount(setup, null, f.Doc));
        if (!sameReceipt) Assert.Equal(0, await EventCount(setup, null, secondDoc));
    }

    [Fact]
    public async Task ConcurrentWaiter_CanUseQuantityAfterFirstCallerRollsBack()
    {
        await using var setup = await Open();
        var f = await Seed(setup, null, 10, 10);
        var secondDoc = await Receipt(setup, null, f.Order);
        await ReceiptLine(setup, null, secondDoc, f.Item, f.OrderLine, 10);
        await using var first = await Open();
        await using var tx = await first.BeginTransactionAsync();
        await Assign(first, tx, f.Doc);
        await using var second = await Open();
        var pending = Assign(second, null, secondDoc);
        await WaitForLock(setup, second.ProcessID);
        await tx.RollbackAsync();
        Assert.Equal(10, await pending);
        Assert.Equal(0, await EventCount(setup, null, f.Doc));
        Assert.Equal(1, await EventCount(setup, null, secondDoc));
    }

    private const string At = "2026-11-13T12:00:00.000Z";

    private static Task<long> Assign(NpgsqlConnection db, NpgsqlTransaction? tx, long doc) =>
        Scalar<long>(db, tx, "SELECT marking_synthetic_quantity_assign_prd(@d,'postgres-regression');", ("d", doc));

    private static Task<long> EventCount(NpgsqlConnection db, NpgsqlTransaction? tx, long doc) =>
        Scalar<long>(db, tx, "SELECT COUNT(*) FROM marking_synthetic_quantity_event WHERE receipt_doc_id=@d;", ("d", doc));

    private static Task<long> Free(NpgsqlConnection db, NpgsqlTransaction? tx, Guid pool) =>
        Scalar<long>(db, tx, "SELECT available_quantity FROM marking_synthetic_quantity_balance WHERE pool_id=@p;", ("p", pool));

    private static Task<long> Applied(NpgsqlConnection db, NpgsqlTransaction? tx, Guid pool, long doc, long line) =>
        Scalar<long>(db, tx, "SELECT COALESCE(SUM(applied_quantity),0)::bigint FROM marking_synthetic_quantity_receipt_balance WHERE pool_id=@p AND receipt_doc_id=@d AND receipt_line_id=@l;", ("p", pool), ("d", doc), ("l", line));

    private static async Task Rejected(NpgsqlConnection db, NpgsqlTransaction tx, Func<Task> action, string message)
    {
        await Execute(db, tx, "SAVEPOINT rejected;");
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal("23514", ex.SqlState);
        Assert.Contains(message, ex.MessageText);
        await Execute(db, tx, "ROLLBACK TO SAVEPOINT rejected; RELEASE SAVEPOINT rejected;");
    }

    private static async Task WaitForLock(NpgsqlConnection observer, int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await Scalar<bool>(observer, null, "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock');", ("pid", pid))) return;
            await Task.Delay(25);
        }
        Assert.Fail("Second PostgreSQL session did not reach an actual lock wait.");
    }

    private static Task<long> NewOrder(NpgsqlConnection db, NpgsqlTransaction? tx) =>
        Scalar<long>(db, tx, "INSERT INTO orders(order_ref,order_type,status,created_at) VALUES (@r,'INTERNAL','IN_PROGRESS',@at) RETURNING id;", ("r", "TEST-200-" + Guid.NewGuid()), ("at", At));

    private static Task<long> Receipt(NpgsqlConnection db, NpgsqlTransaction? tx, long order) =>
        Scalar<long>(db, tx, "INSERT INTO docs(doc_ref,type,status,order_id,created_at) VALUES (@r,'PRODUCTION_RECEIPT','DRAFT',@o,@at) RETURNING id;", ("r", "TEST-200-" + Guid.NewGuid()), ("o", order), ("at", At));

    private static Task<long> ReceiptLine(NpgsqlConnection db, NpgsqlTransaction? tx, long doc, long item, long orderLine, int qty) =>
        Scalar<long>(db, tx, "INSERT INTO doc_lines(doc_id,item_id,order_line_id,qty) VALUES (@d,@i,@l,@q) RETURNING id;", ("d", doc), ("i", item), ("l", orderLine), ("q", qty));

    private sealed record Fixture(long Order, long OrderLine, long Item, string Gtin, long Doc, long Line, Guid Task, Guid Pool);

    private static async Task<Fixture> Seed(NpgsqlConnection db, NpgsqlTransaction? tx, int required, int available)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var gtin = "0460" + (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) % 10000000000UL).ToString("D10", CultureInfo.InvariantCulture);
        var type = await Scalar<long>(db, tx, "INSERT INTO item_types(name,code,enable_marking) VALUES (@s,@s,TRUE) RETURNING id;", ("s", "TEST-200-" + suffix));
        var item = await Scalar<long>(db, tx, "INSERT INTO items(name,barcode,gtin,item_type_id) VALUES (@s,@s,@g,@t) RETURNING id;", ("s", "TEST-200-" + suffix), ("g", gtin), ("t", type));
        var order = await NewOrder(db, tx);
        var ol = await Scalar<long>(db, tx, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,100) RETURNING id;", ("o", order), ("i", item));
        var doc = await Receipt(db, tx, order);
        var line = await ReceiptLine(db, tx, doc, item, ol, required);
        var task = Guid.NewGuid();
        var pool = Guid.NewGuid();
        var f = new Fixture(order, ol, item, gtin, doc, line, task, pool);
        await Issue(db, tx, f, task, pool, "PRODUCTION_ORDER", order, ol, available);
        return f;
    }

    private static async Task<Guid> Pool(NpgsqlConnection db, NpgsqlTransaction? tx, Fixture f, string source, long? order, long? line, int qty)
    {
        var pool = Guid.NewGuid();
        await Issue(db, tx, f, Guid.NewGuid(), pool, source, order, line, qty);
        return pool;
    }

    private static async Task Issue(NpgsqlConnection db, NpgsqlTransaction? tx, Fixture f, Guid task, Guid pool, string source, long? order, long? line, int qty)
    {
        await Execute(db, tx, """
INSERT INTO marking_order(id,item_id,gtin,order_line_id,requested_quantity,request_number,status,source_type,source_order_id,created_at,updated_at)
VALUES (@t,@i,@g,@l::bigint,100,@r,'Printed',@s,@o::bigint,@at,@at);
SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'ISSUE',@q,@t,'test-200',NULL,NULL,'postgres-regression');
""", ("t", task), ("i", f.Item), ("g", f.Gtin), ("l", line), ("r", "TEST-200-" + task), ("s", source), ("o", order), ("at", At), ("op", Guid.NewGuid()), ("p", pool), ("q", (long)qty));
    }

    private static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.CommandTimeout = 20;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }

    private static async Task Execute(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        await using var cmd = Command(db, tx, sql, args);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        await using var cmd = Command(db, tx, sql, args);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<NpgsqlConnection> Open()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required.");
        var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        if (db.Database != "flowstock_test")
        {
            await db.DisposeAsync();
            throw new InvalidOperationException("Quantity PRD tests require disposable flowstock_test.");
        }
        return db;
    }
}
