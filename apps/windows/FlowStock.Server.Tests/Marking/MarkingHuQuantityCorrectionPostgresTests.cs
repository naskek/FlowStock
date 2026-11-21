using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingHuQuantityCorrectionPostgresTests
{
    private static string Connection => Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")!;
    private static ProductionPalletFillingCorrectionService Corrections(bool enabled = true) => new(new PostgresDataStore(Connection), enabled);
    private static ProductionPalletFillingCorrectionConfirmRequest Request(string hu, Guid? id = null, string reason = "Исправить ошибочное наполнение") => new()
    {
        RequestId = (id ?? Guid.NewGuid()).ToString(), HuCode = hu,
        ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled, ReasonText = reason
    };

    [Fact]
    public async Task QuantityCorrection_DefaultGateBlocks_EnabledReleasesExactlyOnce_WithImmutableAudit()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        var hu = await Fill(db, f);
        var before = await History(db, f.Doc);
        Assert.False(Corrections(false).Preview(hu).CanConfirm);
        var request = Request(hu);
        Assert.False(Corrections(false).Confirm(request).Success);
        Assert.Equal(0, await Free(db, null, f.Pool));
        var preview = Corrections().Preview(hu);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Blockers.Select(b => b.Message)));
        var result = Corrections().Confirm(request);
        Assert.True(result.Success, result.Message);
        Assert.Equal(result.AdjustmentId, Corrections().Confirm(request).AdjustmentId);
        Assert.Equal(ProductionPalletFillingCorrectionErrorCodes.IdempotencyKeyReused,
            Corrections().Confirm(Request(hu, Guid.Parse(request.RequestId), "Другая причина")).ErrorCode);
        Assert.Equal(10, await Free(db, null, f.Pool));
        Assert.Equal(0, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(before, await History(db, f.Doc));
        Assert.Equal(1, await AuditCount(db, result.AdjustmentId!.Value));
        Assert.Equal(0d, await Scalar<double>(db, null, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
        Assert.Equal("CLOSED", await Scalar<string>(db, null, "SELECT status FROM docs WHERE id=@d", ("d", f.Doc)));
        Assert.Equal(10d, await Scalar<double>(db, null, "SELECT qty FROM doc_lines WHERE id=@l", ("l", f.Line)));
        foreach (var sql in new[] {
            "UPDATE production_marking_quantity_release_audit SET cor_doc_id=cor_doc_id WHERE adjustment_id=@a",
            "DELETE FROM production_marking_quantity_release_audit WHERE adjustment_id=@a",
            "TRUNCATE production_marking_quantity_release_audit" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(db, null, sql, ("a", result.AdjustmentId)));
            Assert.Equal("55000", ex.SqlState);
        }
        // A new receipt can spend only the released available amount.
        var next = await Receipt(db, null, f.Order);
        var nextLine = await ReceiptLine(db, null, next, f.Item, f.OrderLine, 10);
        await ReadyLines(db, f with { Doc = next });
        Assert.True(new DocumentService(new PostgresDataStore(Connection), true).TryCloseDoc(next, false).Success);
        Assert.Equal(0, await Free(db, null, f.Pool));
        Assert.Equal(10, await Applied(db, null, f.Pool, next, nextLine));
        Assert.Equal(0, await Applied(db, null, f.Pool, f.Doc, f.Line));
    }

    [Fact]
    public async Task SeveralLinesAndPools_ReleaseOnlyExactReceipt_PreserveOtherReceipt()
    {
        await using var db = await Open();
        var f = await Ready(db, 6, 3);
        var ol = await Scalar<long>(db, null, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,4) RETURNING id", ("o", f.Order), ("i", f.Item));
        await ReceiptLine(db, null, f.Doc, f.Item, ol, 4);
        var shared = await Pool(db, null, f, "PRODUCTION_NEED", null, null, 12);
        await ReadyLines(db, f);
        var hu = await Fill(db, f);
        var other = await Receipt(db, null, f.Order);
        var otherLine = await ReceiptLine(db, null, other, f.Item, f.OrderLine, 5);
        await ReadyLines(db, f with { Doc = other });
        Assert.True(new DocumentService(new PostgresDataStore(Connection), true).TryCloseDoc(other, false).Success);
        var result = Corrections().Confirm(Request(hu));
        Assert.True(result.Success, result.Message);
        Assert.Equal(3, await Free(db, null, f.Pool));
        Assert.Equal(7, await Free(db, null, shared));
        Assert.Equal(5, await Applied(db, null, shared, other, otherLine));
        Assert.Equal(3, await AuditCount(db, result.AdjustmentId!.Value));
    }

    [Theory]
    [InlineData("LegacySynthetic")]
    [InlineData("RealImport")]
    public async Task IndependentHybridCodes_KeepTheirPerCodeRollbackAndOrigin(string origin)
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 0);
        await Issue(db, null, f, f.Task, f.Pool, "PRODUCTION_ORDER", f.Order, null, 6);
        await Codes(db, f, 4, origin, general: false);
        var hu = await Fill(db, f);
        var result = Corrections().Confirm(Request(hu));
        Assert.True(result.Success, result.Message);
        Assert.Equal(6, await Free(db, null, f.Pool));
        Assert.Equal(4, await Scalar<long>(db, null, "SELECT COUNT(*) FROM production_marking_transition_audit WHERE adjustment_id=@a AND origin=@origin", ("a", result.AdjustmentId), ("origin", origin)));
        Assert.Equal(4, await Scalar<long>(db, null, "SELECT COUNT(*) FROM marking_code c JOIN marking_order mo ON mo.id=c.marking_order_id WHERE mo.item_id=@i AND c.origin=@origin AND c.status='Reserved' AND c.receipt_doc_id IS NULL AND c.receipt_line_id IS NULL", ("i", f.Item), ("origin", origin)));
    }

    [Theory]
    [InlineData("source-order")]
    [InlineData("source-line")]
    [InlineData("gtin")]
    [InlineData("partial-release")]
    [InlineData("unproven-assignment")]
    [InlineData("wrong-doc")]
    [InlineData("foreign-code-binding")]
    [InlineData("mixed-task")]
    public async Task InvalidHistoryOrSource_FailsClosedWithoutCorOrRelease(string fault)
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        if (fault == "unproven-assignment")
            await Execute(db, null, "SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'ASSIGN',10,NULL,NULL,@d,@l,'external')", ("op", Guid.NewGuid()), ("p", f.Pool), ("d", f.Doc), ("l", f.Line));
        var hu = await Fill(db, f, close: fault != "unproven-assignment");
        if (fault == "source-order") await Execute(db, null, "UPDATE marking_order SET source_order_id=@o WHERE id=@t", ("o", await NewOrder(db, null)), ("t", f.Task));
        if (fault == "source-line") await Execute(db, null, "UPDATE marking_order SET order_line_id=NULL WHERE id=@t", ("t", f.Task));
        if (fault == "gtin") await Execute(db, null, "UPDATE items SET gtin=@g WHERE id=@i", ("g", "invalid-" + Guid.NewGuid()), ("i", f.Item));
        if (fault == "partial-release") await Execute(db, null, "SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'RELEASE',1,NULL,NULL,@d,@l,'external')", ("op", Guid.NewGuid()), ("p", f.Pool), ("d", f.Doc), ("l", f.Line));
        if (fault == "wrong-doc") await Execute(db, null, "UPDATE doc_lines SET doc_id=@d WHERE id=@l", ("d", await Receipt(db, null, f.Order)), ("l", f.Line));
        if (fault is "foreign-code-binding" or "mixed-task")
        {
            if (fault == "foreign-code-binding") await Codes(db, f, 1, "RealImport", general: true);
            var task = fault == "mixed-task" ? f.Task : await Scalar<Guid>(db, null,
                "SELECT id FROM marking_order WHERE item_id=@i AND id<>@t", ("i", f.Item), ("t", f.Task));
            if (fault == "mixed-task")
                await Execute(db, null, """
INSERT INTO marking_code_import(id,original_filename,storage_path,file_hash,source_type,matched_marking_order_id,status,created_at)
VALUES (@id,'test.csv','<test>',@r,'csv',@t,'Imported',@at);
INSERT INTO marking_code(id,code,code_hash,gtin,marking_order_id,import_id,status,created_at,updated_at,origin)
VALUES (@id,@r,@r,@g,@t,@id,'Voided',@at,@at,'RealImport');
""", ("id", Guid.NewGuid()), ("r", "TEST-209-" + Guid.NewGuid()), ("t", task), ("g", f.Gtin), ("at", At));
            else
                await Execute(db, null, "UPDATE marking_code SET receipt_line_id=@l,receipt_doc_id=@d,status='Applied' WHERE marking_order_id=@t",
                    ("l", f.Line), ("d", await Receipt(db, null, f.Order)), ("t", task));
        }
        var before = await EventCount(db, null, f.Doc);
        var request = Request(hu);
        Assert.False(Corrections().Preview(hu).CanConfirm);
        Assert.False(Corrections().Confirm(request).Success);
        Assert.Equal(before, await EventCount(db, null, f.Doc));
        Assert.Equal(0, await Scalar<long>(db, null, "SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE request_id=@r", ("r", Guid.Parse(request.RequestId))));
    }

    [Fact]
    public async Task HistoricalKmReceipt_RemainsUntouched_AndBlocksQuantitativeCorrection()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 8);
        var batch = await Scalar<long>(db, null, "INSERT INTO km_code_batch(order_id,file_hash,imported_at,total_codes) VALUES (@o,@h,@at,2) RETURNING id",
            ("o", f.Order), ("h", Guid.NewGuid().ToString()), ("at", At));
        await Execute(db, null, "INSERT INTO km_code(batch_id,code_raw,gtin14,sku_id,status,receipt_doc_id,receipt_line_id,order_id) SELECT @b,@raw || n,@g,@i,1,@d,@l,@o FROM generate_series(1,2) n",
            ("b", batch), ("raw", Guid.NewGuid().ToString()), ("g", f.Gtin), ("i", f.Item), ("d", f.Doc), ("l", f.Line), ("o", f.Order));
        var hu = await Fill(db, f);
        var snapshot = await Scalar<string>(db, null, "SELECT jsonb_agg(to_jsonb(c) ORDER BY id)::text FROM km_code c WHERE batch_id=@b", ("b", batch));
        Assert.False(Corrections().Confirm(Request(hu)).Success);
        Assert.Equal(8, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(snapshot, await Scalar<string>(db, null, "SELECT jsonb_agg(to_jsonb(c) ORDER BY id)::text FROM km_code c WHERE batch_id=@b", ("b", batch)));
    }

    [Fact]
    public async Task FailureAfterRelease_RollsBackCorCodesQuantityReplacementAndClaim()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 0);
        await Issue(db, null, f, f.Task, f.Pool, "PRODUCTION_ORDER", f.Order, null, 6);
        await Codes(db, f, 4, "RealImport", general: false);
        var hu = await Fill(db, f);
        var request = Request(hu);
        var trigger = "fail209_" + Guid.NewGuid().ToString("N");
        await Execute(db, null, $"CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.status='CORRECTED' THEN RAISE EXCEPTION 'fault-after-release'; END IF; RETURN NEW; END $$; CREATE TRIGGER {trigger} BEFORE UPDATE ON production_pallets FOR EACH ROW EXECUTE FUNCTION {trigger}();");
        try { Assert.Throws<PostgresException>(() => Corrections().Confirm(request)); }
        finally { await Execute(db, null, $"DROP TRIGGER {trigger} ON production_pallets; DROP FUNCTION {trigger}();"); }
        Assert.Equal(0, await Free(db, null, f.Pool));
        Assert.Equal(6, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(4, await BoundCodes(db, f));
        Assert.Equal(0, await Scalar<long>(db, null, "SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE request_id=@r", ("r", Guid.Parse(request.RequestId))));
        Assert.Equal(10d, await Scalar<double>(db, null, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
        Assert.True(Corrections().Confirm(request).Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentCorrections_WaitAndRecheck_WithoutDoubleRelease(bool sameRequest)
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        var hu = await Fill(db, f);
        var request = Request(hu);
        using var applied = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ProductionPalletFillingCorrectionResult? result = null;
        var first = Task.Run(() => new PostgresDataStore(Connection).ExecuteInTransaction(store =>
        {
            result = new ProductionPalletFillingCorrectionService(store, true).Confirm(request);
            Assert.True(result.Success, result.Message);
            applied.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        }));
        Assert.True(applied.Wait(TimeSpan.FromSeconds(15)));
        var app = "hu209-" + Guid.NewGuid();
        var second = Task.Run(() => new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(Connection + ";Application Name=" + app), true)
            .Confirm(sameRequest ? request : Request(hu)));
        try
        {
            await WaitForLock(db, app);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, await Free(db, null, f.Pool));
        }
        finally { release.Set(); }
        await first;
        var other = await second;
        Assert.Equal(sameRequest, other.Success);
        if (sameRequest) Assert.Equal(result!.AdjustmentId, other.AdjustmentId);
        Assert.Equal(10, await Free(db, null, f.Pool));
        Assert.Equal(1, await AuditCount(db, result!.AdjustmentId!.Value));
    }

    [Fact]
    public async Task TsdFillCorrectionRefillCorrection_UsesNewReceiptAndRevisionEachTime()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        await Execute(db, null, "UPDATE order_lines SET qty_ordered=10 WHERE id=@l; UPDATE items SET max_qty_per_hu=5 WHERE id=@i; UPDATE client_blocks SET is_enabled=TRUE WHERE block_key='pc_hu_correction'",
            ("l", f.OrderLine), ("i", f.Item));
        var store = new PostgresDataStore(Connection);
        var filling = new ProductionPalletService(store,
            new ProductionFillCloseService(store, new DocumentService(store, true), new FlowStockLedgerFlowOptions()));
        filling.Plan(f.Doc);
        var pallet = store.GetProductionPalletsByDoc(f.Doc).OrderBy(p => p.Id).First();
        var fill = filling.Fill(pallet.HuCode, "TEST-209", f.Order, f.Doc);
        Assert.True(fill.Success, fill.Error);
        Assert.True(fill.PrdAutoClosed);
        var first = Corrections().Confirm(Request(pallet.HuCode));
        Assert.True(first.Success, first.Message);
        Assert.Equal(fill.ClosedPrdDocId, first.SourcePrdDocId);
        Assert.Equal(10, await Free(db, null, f.Pool));
        var refill = filling.Fill(pallet.HuCode, "TEST-209", f.Order, first.ReplacementPrdDocId);
        Assert.True(refill.Success, refill.Error);
        Assert.True(refill.PrdAutoClosed);
        Assert.NotEqual(fill.ClosedPrdDocId, refill.ClosedPrdDocId);
        var second = Corrections().Confirm(Request(pallet.HuCode));
        Assert.True(second.Success, second.Message);
        Assert.Equal(refill.ClosedPrdDocId, second.SourcePrdDocId);
        Assert.NotEqual(first.SourcePalletId, second.SourcePalletId);
        Assert.Equal(10, await Free(db, null, f.Pool));
        Assert.Equal(1, await AuditCount(db, first.AdjustmentId!.Value));
        Assert.Equal(1, await AuditCount(db, second.AdjustmentId!.Value));
        Assert.Equal(first.AdjustmentId, await Scalar<long>(db, null,
            "SELECT predecessor_adjustment_id FROM production_pallet_filling_adjustments WHERE id=@a", ("a", second.AdjustmentId)));
    }

    [Fact]
    public async Task ConsumerWaitsForUncommittedRelease_ThenSpendsFreshBalance()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        var hu = await Fill(db, f);
        var next = await Receipt(db, null, f.Order);
        var line = await ReceiptLine(db, null, next, f.Item, f.OrderLine, 10);
        using var corrected = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => new PostgresDataStore(Connection).ExecuteInTransaction(store =>
        {
            Assert.True(new ProductionPalletFillingCorrectionService(store, true).Confirm(Request(hu)).Success);
            corrected.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        }));
        Assert.True(corrected.Wait(TimeSpan.FromSeconds(15)));
        var app = "hu209-consumer-" + Guid.NewGuid();
        var consumer = Task.Run(async () =>
        {
            await using var waiting = new NpgsqlConnection(Connection + ";Application Name=" + app);
            await waiting.OpenAsync();
            await Execute(waiting, null, "SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'ASSIGN',10,NULL,NULL,@d,@l,'consumer')",
                ("op", Guid.NewGuid()), ("p", f.Pool), ("d", next), ("l", line));
        });
        try { await WaitForLock(db, app); Assert.False(consumer.IsCompleted); }
        finally { release.Set(); }
        await first;
        await consumer;
        Assert.Equal(0, await Free(db, null, f.Pool));
        Assert.Equal(10, await Applied(db, null, f.Pool, next, line));
        Assert.Equal(0, await Applied(db, null, f.Pool, f.Doc, f.Line));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPartialReplacement_IgnoresReleasedPredecessor_BlocksCurrentAssignment(bool hasCurrentAssignment)
    {
        await using var db = await Open();
        var f = await Ready(db, 6, 10);
        var ol = await Scalar<long>(db, null, "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES (@o,@i,4) RETURNING id", ("o", f.Order), ("i", f.Item));
        await ReceiptLine(db, null, f.Doc, f.Item, ol, 4);
        await Pool(db, null, f, "PRODUCTION_NEED", null, null, 4);
        await ReadyLines(db, f);
        var hu = await Fill(db, f);
        var correction = Corrections().Confirm(Request(hu));
        Assert.True(correction.Success, correction.Message);
        await Execute(db, null, "UPDATE production_pallet_lines SET filled_qty=planned_qty,filled_at=@at WHERE production_pallet_id=@p AND order_line_id=@l",
            ("p", correction.ReplacementPalletId), ("l", f.OrderLine), ("at", At));
        if (hasCurrentAssignment)
            await Assign(db, null, correction.ReplacementPrdDocId!.Value);
        var reset = new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(), HuCode = hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.ResetPartial, ReasonText = "Сброс partial"
        };
        Assert.Equal(!hasCurrentAssignment, Corrections().Preview(hu).CanConfirm);
        Assert.Equal(!hasCurrentAssignment, Corrections().Confirm(reset).Success);
        Assert.Equal(0, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(0, await Scalar<long>(db, null, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d", ("d", correction.ReplacementPrdDocId)));
    }

    [Fact]
    public async Task CallerRollback_AndLineLockConflict_LeaveAllFactsUnchanged()
    {
        await using var db = await Open();
        var f = await Ready(db, 10, 10);
        var hu = await Fill(db, f);
        var request = Request(hu);
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(Connection).ExecuteInTransaction(store =>
        {
            Assert.True(new ProductionPalletFillingCorrectionService(store, true).Confirm(request).Success);
            throw new InvalidOperationException("caller rollback");
        }));
        await using (var tx = await db.BeginTransactionAsync())
        {
            await Execute(db, tx, "LOCK TABLE doc_lines IN ROW EXCLUSIVE MODE");
            var ex = Assert.Throws<PostgresException>(() => Corrections().Confirm(request));
            Assert.Equal("55P03", ex.SqlState);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await Free(db, null, f.Pool));
        Assert.Equal(10, await Applied(db, null, f.Pool, f.Doc, f.Line));
        Assert.Equal(0, await Scalar<long>(db, null, "SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE request_id=@r", ("r", Guid.Parse(request.RequestId))));
        Assert.True(Corrections().Confirm(request).Success);
    }

    private static async Task WaitForLock(NpgsqlConnection db, string app)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (await Scalar<long>(db, null, "SELECT COUNT(*) FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND cardinality(pg_blocking_pids(pid))>0", ("app", app)) > 0)
                return;
            await Task.Delay(20);
        }
        Assert.Fail("Concurrent operation did not reach a real PostgreSQL lock wait.");
    }

    private static async Task<string> Fill(NpgsqlConnection db, Fixture f, bool close = true)
    {
        if (close)
        {
            var result = new DocumentService(new PostgresDataStore(Connection), true).TryCloseDoc(f.Doc, false);
            Assert.True(result.Success, string.Join(";", result.Errors));
        }
        else
            await Execute(db, null, "UPDATE docs SET status='CLOSED',closed_at=@at WHERE id=@d; INSERT INTO ledger(ts,doc_id,item_id,location_id,qty_delta,hu_code,hu) SELECT @at,doc_id,item_id,to_location_id,qty,to_hu,to_hu FROM doc_lines WHERE doc_id=@d;", ("at", At), ("d", f.Doc));
        await Execute(db, null, """
UPDATE client_blocks SET is_enabled=TRUE WHERE block_key='pc_hu_correction';
INSERT INTO production_pallets(prd_doc_id,doc_line_id,order_id,order_line_id,item_id,hu_code,planned_qty,to_location_id,status,pallet_no,pallet_count,filled_at,created_at)
SELECT doc_id,id,@o,order_line_id,item_id,to_hu,qty,to_location_id,'FILLED',1,1,@at,@at FROM doc_lines WHERE id=@l;
INSERT INTO production_pallet_lines(production_pallet_id,doc_line_id,order_line_id,item_id,planned_qty,filled_qty,filled_at,created_at)
SELECT p.id,dl.id,dl.order_line_id,dl.item_id,dl.qty,dl.qty,@at,@at FROM production_pallets p JOIN doc_lines dl ON dl.doc_id=p.prd_doc_id WHERE p.prd_doc_id=@d;
""", ("o", f.Order), ("l", f.Line), ("at", At), ("d", f.Doc));
        return await Scalar<string>(db, null, "SELECT to_hu FROM doc_lines WHERE id=@l", ("l", f.Line));
    }

    private static Task<long> AuditCount(NpgsqlConnection db, long adjustment) => Scalar<long>(db, null,
        "SELECT COUNT(*) FROM production_marking_quantity_release_audit WHERE adjustment_id=@a", ("a", adjustment));
    private static Task<string> History(NpgsqlConnection db, long doc) => Scalar<string>(db, null,
        "SELECT jsonb_agg(to_jsonb(e) ORDER BY id)::text FROM marking_synthetic_quantity_event e WHERE receipt_doc_id=@d AND action='ASSIGN'", ("d", doc));

    private static async Task<Fixture> Ready(NpgsqlConnection db, int required, int available)
    {
        var f = await Seed(db, null, required, available);
        await ReadyLines(db, f);
        return f;
    }

    private static async Task ReadyLines(NpgsqlConnection db, Fixture f)
    {
        var loc = await Scalar<long>(db, null, "INSERT INTO locations(code,name) VALUES (@r,'TEST-209') RETURNING id", ("r", "TEST-209-" + Guid.NewGuid()));
        await Execute(db, null, "UPDATE doc_lines SET to_location_id=@loc,to_hu=@hu WHERE doc_id=@d", ("loc", loc), ("hu", "TEST-209-" + Guid.NewGuid()), ("d", f.Doc));
    }

    private static async Task Codes(NpgsqlConnection db, Fixture f, int qty, string origin, bool general)
    {
        var task = Guid.NewGuid();
        var import = Guid.NewGuid();
        var temporary = origin is "LegacySynthetic" or "HistoricalUnknown";
        await Execute(db, null, """
INSERT INTO marking_order(id,item_id,gtin,order_line_id,requested_quantity,request_number,status,source_type,source_order_id,created_at,updated_at)
VALUES (@t,@i,@g,@l::bigint,@q,@r,'Printed',@s,@o::bigint,@at,@at);
INSERT INTO marking_code_import(id,original_filename,storage_path,file_hash,source_type,matched_marking_order_id,status,created_at)
VALUES (@id,'test.csv','<test>',@r,@source,@t,'Imported',@at);
INSERT INTO marking_code(id,code,code_hash,gtin,marking_order_id,import_id,status,created_at,updated_at,origin)
SELECT gen_random_uuid(),@r || n,@r || n,@g,@t,@id,'Reserved',@at,@at,@origin FROM generate_series(1,@q) n;
""", ("t", task), ("i", f.Item), ("g", f.Gtin), ("l", general ? null : f.OrderLine), ("q", qty), ("r", (temporary ? "TEMP-CHZ-" : "TEST-209-") + task), ("source", temporary ? "temporary-chz-export" : "csv"), ("s", general ? "PRODUCTION_NEED" : "PRODUCTION_ORDER"), ("o", general ? null : f.Order), ("at", At), ("id", import), ("origin", origin));
    }

    private static Task<long> BoundCodes(NpgsqlConnection db, Fixture f) => Scalar<long>(db, null, "SELECT COUNT(*) FROM marking_code WHERE receipt_doc_id=@d AND receipt_line_id=@l AND status='Applied'", ("d", f.Doc), ("l", f.Line));
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
        Scalar<long>(db, tx, "INSERT INTO orders(order_ref,order_type,status,created_at) VALUES (@r,'INTERNAL','IN_PROGRESS',@at) RETURNING id;", ("r", "TEST-209-" + Guid.NewGuid()), ("at", At));

    private static Task<long> Receipt(NpgsqlConnection db, NpgsqlTransaction? tx, long order) =>
        Scalar<long>(db, tx, "INSERT INTO docs(doc_ref,type,status,order_id,created_at) VALUES (@r,'PRODUCTION_RECEIPT','DRAFT',@o,@at) RETURNING id;", ("r", "TEST-209-" + Guid.NewGuid()), ("o", order), ("at", At));

    private static Task<long> ReceiptLine(NpgsqlConnection db, NpgsqlTransaction? tx, long doc, long item, long orderLine, int qty) =>
        Scalar<long>(db, tx, "INSERT INTO doc_lines(doc_id,item_id,order_line_id,qty) VALUES (@d,@i,@l,@q) RETURNING id;", ("d", doc), ("i", item), ("l", orderLine), ("q", qty));

    private sealed record Fixture(long Order, long OrderLine, long Item, string Gtin, long Doc, long Line, Guid Task, Guid Pool);

    private static async Task<Fixture> Seed(NpgsqlConnection db, NpgsqlTransaction? tx, int required, int available)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var gtin = "0460" + (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) % 10000000000UL).ToString("D10", CultureInfo.InvariantCulture);
        var type = await Scalar<long>(db, tx, "INSERT INTO item_types(name,code,enable_marking) VALUES (@s,@s,TRUE) RETURNING id;", ("s", "TEST-209-" + suffix));
        var item = await Scalar<long>(db, tx, "INSERT INTO items(name,barcode,gtin,item_type_id) VALUES (@s,@s,@g,@t) RETURNING id;", ("s", "TEST-209-" + suffix), ("g", gtin), ("t", type));
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
SELECT * FROM marking_synthetic_quantity_apply(@op,@p,'ISSUE',@q,@t,'test-209',NULL,NULL,'postgres-regression');
""", ("t", task), ("i", f.Item), ("g", f.Gtin), ("l", line), ("r", "TEST-209-" + task), ("s", source), ("o", order), ("at", At), ("op", Guid.NewGuid()), ("p", pool), ("q", (long)qty));
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
        var value = (await cmd.ExecuteScalarAsync())!;
        return value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
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
