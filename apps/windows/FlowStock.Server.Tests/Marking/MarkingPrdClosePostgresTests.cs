using System.Globalization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingPrdClosePostgresTests
{
    private static string Connection => Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")!;
    private static DocumentService Documents() => new(new PostgresDataStore(Connection), quantityPrdCloseEnabled: true);

    [Fact]
    public async Task SamePhysicalRealCodeAcrossHistoricalTables_CannotCountTwice()
    {
        await using var db = await Open();
        var f = await Ready(db, 6, 2);
        await Codes(db, f, 2, "RealImport", general: true);
        var batch = await Scalar<long>(db, null, "INSERT INTO km_code_batch(order_id,file_hash,imported_at,total_codes) VALUES (@o,@h,@at,2) RETURNING id", ("o", f.Order), ("h", Guid.NewGuid().ToString()), ("at", At));
        await Execute(db, null, "INSERT INTO km_code(batch_id,code_raw,gtin14,sku_id,status,receipt_doc_id,receipt_line_id,order_id) SELECT @b,c.code,@g,@i,1,@d,@l,@o FROM marking_code c JOIN marking_order mo ON mo.id=c.marking_order_id WHERE mo.item_id=@i", ("b", batch), ("g", f.Gtin), ("i", f.Item), ("d", f.Doc), ("l", f.Line), ("o", f.Order));
        var ex = Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(f.Doc, false));
        Assert.Contains("QUANTITY_PRD_DUPLICATE_REAL_CODE", ex.MessageText);
        await Unchanged(db, f, 2);
        Assert.Equal(0, await BoundCodes(db, f));
    }

    [Fact]
    public async Task SeveralLinesAndPools_AssignExactSourceLine_WithoutCrossLineConsumption()
    {
        await using var db = await Open();
        var f = await Ready(db, 6, 3);
        var orderLine = await Scalar<long>(db, null, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,4) RETURNING id", ("o", f.Order), ("i", f.Item));
        var receiptLine = await ReceiptLine(db, null, f.Doc, f.Item, orderLine, 4);
        var general = await Pool(db, null, f, "PRODUCTION_NEED", null, null, 7);
        await ReadyLines(db, f);
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(3, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(0, await Applied(db, null, f.Pool, f.Doc, receiptLine));
        Assert.Equal(3, await Applied(db, null, general, f.Doc, f.Line));
        Assert.Equal(4, await Applied(db, null, general, f.Doc, receiptLine));
        Assert.Equal(10d, await Scalar<double>(db, null, "SELECT SUM(qty_delta) FROM ledger WHERE doc_id=@d", ("d", f.Doc)));
    }

    [Fact]
    public async Task StagedDraftAssignment_ReplaysIntoRealCloseWithoutAnotherAssign()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        Assert.Equal(10, await Assign(db, null, f.Doc));
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(1, await EventCount(db, null, f.Doc));
    }

    [Fact]
    public async Task ConcurrentDuplicateClose_WaitsForOrder_ThenObservesClosedDocument()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        using var applied = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => new PostgresDataStore(Connection).ExecuteInTransaction(store =>
        {
            Assert.True(new DocumentService(store, true).TryCloseDoc(f.Doc, false).Success);
            applied.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
        }));
        Assert.True(applied.Wait(TimeSpan.FromSeconds(15)));
        var app = "TEST-207-" + Guid.NewGuid();
        var cs = new NpgsqlConnectionStringBuilder(Connection) { ApplicationName = app }.ConnectionString;
        var duplicate = Task.Run(() => new DocumentService(new PostgresDataStore(cs), true).TryCloseDoc(f.Doc, false));
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!await Scalar<bool>(db, null, "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock')", ("app", app)))
            {
                Assert.True(DateTime.UtcNow < deadline, "Duplicate Close never reached an actual lock wait.");
                await Task.Delay(25);
            }
        }
        finally { release.Set(); }
        await first;
        Assert.False((await duplicate).Success);
        Assert.Equal(1, await EventCount(db, null, f.Doc));
        Assert.Equal(1, await Scalar<long>(db, null, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d", ("d", f.Doc)));
    }

    [Fact]
    public async Task TsdAutoClose_DedicatedReceiptAssignsOnlyFilledPallet_ShortageRollsBackNextFill()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 5);
        await Execute(db, null, "UPDATE order_lines SET qty_ordered=10 WHERE id=@l; UPDATE items SET max_qty_per_hu=5 WHERE id=@i", ("l", f.OrderLine), ("i", f.Item));
        await Execute(db, null, "UPDATE doc_lines SET qty=5 WHERE id=@l", ("l", f.Line));
        await ReceiptLine(db, null, f.Doc, f.Item, f.OrderLine, 5);
        await ReadyLines(db, f);
        var store = new PostgresDataStore(Connection);
        var autoClose = new ProductionFillCloseService(store, new DocumentService(store, true), new FlowStockLedgerFlowOptions());
        var service = new ProductionPalletService(store, autoClose);
        service.Plan(f.Doc);
        var pallets = store.GetProductionPalletsByDoc(f.Doc).OrderBy(p => p.Id).ToArray();
        Assert.Equal(2, pallets.Length);
        var first = service.Fill(pallets[0].HuCode, "TEST-207", f.Order, f.Doc);
        Assert.True(first.Success, first.Error);
        Assert.True(first.PrdAutoClosed);
        Assert.NotEqual(f.Doc, first.ClosedPrdDocId);
        var receipt = first.ClosedPrdDocId!.Value;
        Assert.Equal(5, await Scalar<long>(db, null, "SELECT SUM(applied_quantity)::bigint FROM marking_synthetic_quantity_receipt_balance WHERE receipt_doc_id=@d", ("d", receipt)));
        Assert.Equal(5d, await Scalar<double>(db, null, "SELECT SUM(qty_delta) FROM ledger WHERE doc_id=@d", ("d", receipt)));
        var error = Assert.Throws<PostgresException>(() => service.Fill(pallets[1].HuCode, "TEST-207", f.Order, f.Doc));
        Assert.Contains("QUANTITY_PRD_SHORTAGE", error.MessageText);
        Assert.Equal(pallets[1].Status, store.GetProductionPalletByHu(pallets[1].HuCode)!.Status);
        Assert.All(store.GetProductionPalletByHu(pallets[1].HuCode)!.Lines, line => Assert.Equal(0, line.FilledQty));
        Assert.Equal(DocStatus.Draft, store.GetDoc(f.Doc)!.Status);
        Assert.Equal(0, await EventCount(db, null, f.Doc));
        Assert.Equal(2, store.GetDocsByOrder(f.Order).Count);
    }

    [Fact]
    public async Task ExistingRealKmBinding_ContributesOnlyToItsExactLine()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 8);
        var batch = await Scalar<long>(db, null, "INSERT INTO km_code_batch(order_id,file_hash,imported_at,total_codes) VALUES (@o,@h,@at,2) RETURNING id", ("o", f.Order), ("h", Guid.NewGuid().ToString()), ("at", At));
        await Execute(db, null, "INSERT INTO km_code(batch_id,code_raw,gtin14,sku_id,status,receipt_doc_id,receipt_line_id,order_id) SELECT @b,@raw || n,@g,@i,1,@d,@l,@o FROM generate_series(1,2) n", ("b", batch), ("raw", Guid.NewGuid().ToString()), ("g", f.Gtin), ("i", f.Item), ("d", f.Doc), ("l", f.Line), ("o", f.Order));
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(8, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(2, await Scalar<long>(db, null, "SELECT COUNT(*) FROM km_code WHERE receipt_doc_id=@d AND receipt_line_id=@l AND status=1", ("d", f.Doc), ("l", f.Line)));
    }

    [Theory]
    [InlineData("INTERNAL")]
    [InlineData("CUSTOMER")]
    [InlineData("standalone")]
    public async Task LegacyReceipts_RetainOrderAndNullableTaskScopeCompatibility(string type)
    {
        await using var db = await Open();
        var f = await Ready(db, 4, 0);
        await Codes(db, f, 4, "LegacySynthetic", general: true);
        if (type == "standalone")
            await Execute(db, null, "UPDATE docs SET order_id=NULL WHERE id=@d; UPDATE doc_lines SET order_line_id=NULL WHERE doc_id=@d", ("d", f.Doc));
        else
            await Execute(db, null, "UPDATE orders SET order_type=@t WHERE id=@o", ("t", type), ("o", f.Order));
        await Execute(db, null, "UPDATE marking_order SET item_id=NULL WHERE item_id=@i", ("i", f.Item));
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(4, await BoundCodes(db, f));
    }

    [Fact]
    public async Task PerCodeAndQuantityOnSameTask_EvenVoided_FailsClosed()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        await Execute(db, null, """
INSERT INTO marking_code_import(id,original_filename,storage_path,file_hash,source_type,matched_marking_order_id,status,created_at)
VALUES (@id,'test.csv','<test>',@r,'csv',@t,'Imported',@at);
INSERT INTO marking_code(id,code,code_hash,gtin,marking_order_id,import_id,status,created_at,updated_at,origin)
VALUES (@id,@r,@r,@g,@t,@id,'Voided',@at,@at,'RealImport');
""", ("id", Guid.NewGuid()), ("t", f.Task), ("g", f.Gtin), ("r", "TEST-207-" + Guid.NewGuid()), ("at", At));
        var ex = Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(f.Doc, false));
        Assert.Contains("QUANTITY_PRD_PER_CODE_CONFLICT", ex.MessageText);
        await Unchanged(db, f, 10);
    }

    [Fact]
    public async Task QuantityClose_CommitsExactReceiptLedgerAndStatus_RepeatDoesNotDuplicate()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        var result = Documents().TryCloseDoc(f.Doc, false);
        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Equal(10, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(0, await Free(db, null, f.Pool));
        Assert.Equal("CLOSED", await Scalar<string>(db, null, "SELECT status FROM docs WHERE id=@d", ("d", f.Doc)));
        Assert.Equal(10d, await Scalar<double>(db, null, "SELECT SUM(qty_delta) FROM ledger WHERE doc_id=@d", ("d", f.Doc)));
        Assert.False(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(1, await EventCount(db, null, f.Doc));
        Assert.Equal(1, await Scalar<long>(db, null, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d", ("d", f.Doc)));
    }

    [Fact]
    public async Task DefaultGate_RemainsOff_ExistingShadowCloseDoesNotAssignQuantity()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 1);
        var result = new DocumentService(new PostgresDataStore(Connection)).TryCloseDoc(f.Doc, false);
        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Equal(0, await EventCount(db, null, f.Doc));
        Assert.Equal(1, await Free(db, null, f.Pool));
    }

    [Theory]
    [InlineData("LegacySynthetic")]
    [InlineData("RealImport")]
    public async Task PerCodeOnly_ClosePreservesCodesAndReceiptBinding(string origin)
    {
        await using var db = await Open();
        var f = await Ready(db, 4, 0);
        await Codes(db, f, 4, origin, general: false);
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(4, await BoundCodes(db, f));
        Assert.Equal(0, await EventCount(db, null, f.Doc));
    }

    [Fact]
    public async Task IndependentHybrid_UsesOrderQuantityBeforeOlderGeneralRealCodes()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 6);
        await Codes(db, f, 10, "RealImport", general: true);
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(6, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(4, await BoundCodes(db, f));
        Assert.Equal(6, await Scalar<long>(db, null, "SELECT COUNT(*) FROM marking_code c JOIN marking_order mo ON mo.id=c.marking_order_id WHERE mo.item_id=@i AND c.status='Reserved'", ("i", f.Item)));
    }

    [Fact]
    public async Task LateLineShortage_RollsBackEarlierAssignmentsAndAllCloseEffects()
    {
        await using var db = await Open();
        var f = await Ready(db, 6, 10);
        var line = await ReceiptLine(db, null, f.Doc, f.Item, f.OrderLine, 6);
        await ReadyLines(db, f);
        var ex = Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(f.Doc, true));
        Assert.Contains("QUANTITY_PRD_SHORTAGE", ex.MessageText);
        await Unchanged(db, f, 10);
        Assert.Equal(0, await Applied(db, null, f.Pool, f.Doc, line));
    }

    [Fact]
    public async Task FailureAfterAssignDuringLedger_RollsBackPerCodeQuantityAndDocument()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 6);
        await Codes(db, f, 4, "RealImport", general: true);
        var name = "test_207_" + Guid.NewGuid().ToString("N");
        await Execute(db, null, $"CREATE FUNCTION {name}() RETURNS TRIGGER LANGUAGE plpgsql AS $$ BEGIN IF NEW.doc_id={f.Doc} THEN RAISE EXCEPTION 'TEST_207_LEDGER_FAILURE'; END IF; RETURN NEW; END $$; CREATE TRIGGER {name} BEFORE INSERT ON ledger FOR EACH ROW EXECUTE FUNCTION {name}();");
        try
        {
            var ex = Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(f.Doc, false));
            Assert.Contains("TEST_207_LEDGER_FAILURE", ex.MessageText);
            await Unchanged(db, f, 6);
            Assert.Equal(0, await BoundCodes(db, f));
        }
        finally { await Execute(db, null, $"DROP TRIGGER {name} ON ledger; DROP FUNCTION {name}();"); }
        Assert.True(Documents().TryCloseDoc(f.Doc, false).Success);
    }

    [Theory]
    [InlineData("wrong-line")]
    [InlineData("foreign-receipt-line")]
    [InlineData("foreign-order")]
    [InlineData("gtin-drift")]
    [InlineData("cancelled-task")]
    [InlineData("released-history")]
    public async Task InvalidQuantityScope_FailsClosed(string fault)
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        long? foreignLine = null;
        if (fault == "foreign-receipt-line")
        {
            foreignLine = await Scalar<long>(db, null, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,100) RETURNING id", ("o", await NewOrder(db, null)), ("i", f.Item));
            await Execute(db, null, "UPDATE doc_lines SET order_line_id=@l WHERE id=@d", ("l", foreignLine), ("d", f.Line));
        }
        if (fault == "wrong-line")
        {
            var other = await Scalar<long>(db, null, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,100) RETURNING id", ("o", f.Order), ("i", f.Item));
            await Execute(db, null, "UPDATE doc_lines SET order_line_id=@l WHERE id=@d", ("l", other), ("d", f.Line));
        }
        if (fault == "foreign-order") await Execute(db, null, "UPDATE marking_order SET source_order_id=@o WHERE id=@t", ("o", await NewOrder(db, null)), ("t", f.Task));
        if (fault == "gtin-drift") await Execute(db, null, "UPDATE items SET gtin='1' || SUBSTRING(gtin FROM 2) WHERE id=@i", ("i", f.Item));
        if (fault == "cancelled-task") await Execute(db, null, "UPDATE marking_order SET status='Cancelled' WHERE id=@t", ("t", f.Task));
        if (fault == "released-history")
        {
            await Assign(db, null, f.Doc);
            await Execute(db, null, "SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'RELEASE',10,NULL,NULL,@d,@l,'test')", ("op", Guid.NewGuid()), ("p", f.Pool), ("d", f.Doc), ("l", f.Line));
        }
        if (fault == "foreign-receipt-line") Assert.False(Documents().TryCloseDoc(f.Doc, false).Success);
        else Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(f.Doc, false));
        if (foreignLine.HasValue) Assert.Equal(foreignLine.Value, await Scalar<long>(db, null, "SELECT order_line_id FROM doc_lines WHERE id=@l", ("l", f.Line)));
        Assert.Equal(10, await Free(db, null, f.Pool));
        Assert.Equal("DRAFT", await Scalar<string>(db, null, "SELECT status FROM docs WHERE id=@d", ("d", f.Doc)));
        Assert.Equal(0, await Scalar<long>(db, null, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d", ("d", f.Doc)));
    }

    [Fact]
    public async Task CompetingClose_AndDuplicateClose_CannotOverspendOrDuplicateLedger()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        await Execute(db, null, "SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'VOID',10,NULL,NULL,NULL,NULL,'test')", ("op", Guid.NewGuid()), ("p", f.Pool));
        var shared = await Pool(db, null, f, "PRODUCTION_NEED", null, null, 10);
        var otherOrder = await NewOrder(db, null);
        var otherLine = await Scalar<long>(db, null, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,100) RETURNING id", ("o", otherOrder), ("i", f.Item));
        var otherDoc = await Receipt(db, null, otherOrder);
        await ReceiptLine(db, null, otherDoc, f.Item, otherLine, 10);
        var other = f with { Order = otherOrder, OrderLine = otherLine, Doc = otherDoc };
        await ReadyLines(db, other);
        using var applied = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => new PostgresDataStore(Connection).ExecuteInTransaction(store =>
        {
            Assert.True(new DocumentService(store, true).TryCloseDoc(f.Doc, false).Success);
            applied.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        }));
        Assert.True(applied.Wait(TimeSpan.FromSeconds(15)));
        try
        {
            var contention = Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(otherDoc, false));
            Assert.Equal("55P03", contention.SqlState); // No unsafe table-lock upgrade wait.
            Assert.Equal(0, await EventCount(db, null, otherDoc));
        }
        finally { release.Set(); }
        await first;
        var shortage = Assert.Throws<PostgresException>(() => Documents().TryCloseDoc(otherDoc, false));
        Assert.Contains("QUANTITY_PRD_SHORTAGE", shortage.MessageText);
        Assert.False(Documents().TryCloseDoc(f.Doc, false).Success);
        Assert.Equal(0, await Free(db, null, shared));
        Assert.Equal(10, await Applied(db, null, shared, f.Doc, f.Line));
        Assert.Equal(1, await Scalar<long>(db, null, "SELECT COUNT(*) FROM ledger WHERE doc_id IN (@a,@b)", ("a", f.Doc), ("b", otherDoc)));
    }

    private static async Task<Fixture> Ready(NpgsqlConnection db, int required, int available)
    {
        var f = await Seed(db, null, required, available);
        await ReadyLines(db, f);
        return f;
    }

    private static async Task ReadyLines(NpgsqlConnection db, Fixture f)
    {
        var loc = await Scalar<long>(db, null, "INSERT INTO locations(code,name) VALUES (@r,'TEST-207') RETURNING id", ("r", "TEST-207-" + Guid.NewGuid()));
        await Execute(db, null, "UPDATE doc_lines SET to_location_id=@loc,to_hu=@hu || '-' || id WHERE doc_id=@d", ("loc", loc), ("hu", "TEST-207-" + Guid.NewGuid()), ("d", f.Doc));
    }

    private static async Task Codes(NpgsqlConnection db, Fixture f, int qty, string origin, bool general)
    {
        var task = Guid.NewGuid();
        var import = Guid.NewGuid();
        await Execute(db, null, """
INSERT INTO marking_order(id,item_id,gtin,order_line_id,requested_quantity,request_number,status,source_type,source_order_id,created_at,updated_at)
VALUES (@t,@i,@g,@l::bigint,@q,@r,'Printed',@s,@o::bigint,@at,@at);
INSERT INTO marking_code_import(id,original_filename,storage_path,file_hash,source_type,matched_marking_order_id,status,created_at)
VALUES (@id,'test.csv','<test>',@r,'csv',@t,'Imported',@at);
INSERT INTO marking_code(id,code,code_hash,gtin,marking_order_id,import_id,status,created_at,updated_at,origin)
SELECT gen_random_uuid(),@r || n,@r || n,@g,@t,@id,'Reserved',@at,@at,@origin FROM generate_series(1,@q) n;
""", ("t", task), ("i", f.Item), ("g", f.Gtin), ("l", general ? null : f.OrderLine), ("q", qty), ("r", "TEST-207-" + task), ("s", general ? "PRODUCTION_NEED" : "PRODUCTION_ORDER"), ("o", general ? null : f.Order), ("at", At), ("id", import), ("origin", origin));
    }

    private static Task<long> BoundCodes(NpgsqlConnection db, Fixture f) => Scalar<long>(db, null, "SELECT COUNT(*) FROM marking_code WHERE receipt_doc_id=@d AND receipt_line_id=@l AND status='Applied'", ("d", f.Doc), ("l", f.Line));
    private static async Task Unchanged(NpgsqlConnection db, Fixture f, int available)
    {
        Assert.Equal(0, await EventCount(db, null, f.Doc));
        Assert.Equal(available, await Free(db, null, f.Pool));
        Assert.Equal("DRAFT", await Scalar<string>(db, null, "SELECT status FROM docs WHERE id=@d", ("d", f.Doc)));
        Assert.Equal(0, await Scalar<long>(db, null, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d", ("d", f.Doc)));
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
        if (available > 0) await Issue(db, tx, f, task, pool, "PRODUCTION_ORDER", order, ol, available);
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
