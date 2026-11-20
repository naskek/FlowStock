using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

public sealed partial class MarkingHuProspectiveReservationPostgresTests
{
    private static string CorrectionConnection => Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")!;
    private static ProductionPalletFillingCorrectionService AdoptedCorrections(bool enabled = true) =>
        new(new PostgresDataStore(CorrectionConnection), true, enabled);
    private static ProductionPalletFillingCorrectionConfirmRequest CorrectionRequest(string hu) => new()
    {
        RequestId = Guid.NewGuid().ToString(), HuCode = hu,
        ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
        ReasonText = "Исправление ошибочного наполнения adopted HU"
    };

    private static ProductionPalletFillResult FillAdopted(string hu, long order)
    {
        var data = new PostgresDataStore(CorrectionConnection);
        var close = new ProductionFillCloseService(data, new DocumentService(data, true),
            new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true });
        return new ProductionPalletService(data, close).Fill(hu, "COR-REGRESSION", order);
    }

    [Theory]
    [InlineData("LegacySynthetic", 5)]
    [InlineData("RealImport", 5)]
    [InlineData("quantity", 5)]
    [InlineData("LegacySynthetic", 3)]
    [InlineData("RealImport", 3)]
    [InlineData("quantity", 3)]
    [InlineData("quantity", 0)]
    public async Task AdoptedCorrection_RealTsdFill_Cor_Refill_SecondRevision_PreservesSourceAudit(string mode, int coverage)
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, mode, coverage);
        var first = FillAdopted(hu, f.Target);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.True(first.PrdAutoClosed);
        var firstDoc = first.ClosedPrdDocId!.Value;
        var immutable = await CorrectionSnapshot(db, f, firstDoc);
        var issuedBefore = await Scalar<long>(db, null!, "SELECT COUNT(*) FROM marking_synthetic_quantity_event e JOIN marking_synthetic_quantity_pool p ON p.id=e.pool_id JOIN marking_order mo ON mo.id=p.marking_order_id WHERE mo.source_order_id IN (@source,@target) AND e.action='ISSUE'", ("source", f.Source), ("target", f.Target));
        if (coverage > 0)
            Assert.Contains(AdoptedCorrections(false).Preview(hu).Blockers,
                b => b.Code == ProductionPalletFillingCorrectionErrorCodes.AdoptedHuMarkingConsumed);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var preview = AdoptedCorrections().Preview(hu);
            Assert.True(preview.CanConfirm, string.Join("; ", preview.Blockers.Select(b => b.Code + ":" + b.Message)));
            var request = CorrectionRequest(hu);
            var cor = AdoptedCorrections().Confirm(request);
            Assert.True(cor.Success, cor.Message);
            Assert.True(AdoptedCorrections().Confirm(request).Replay);
            Assert.Equal(cor.AdjustmentId, AdoptedCorrections().Confirm(request).AdjustmentId);
            Assert.Equal(coverage, await Scalar<long>(db, null!, """
SELECT COALESCE(SUM(quantity),0)::bigint FROM marking_hu_current_reservation r
JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id
WHERE tl.transfer_id=@transfer AND r.state='ACTIVE';
""", ("transfer", f.Transfer)));
            Assert.Equal((long)coverage, await ReadCustomerCoverage(db, null!, f.Target));
            var excel = new OrderMarkingExportService(new PostgresDataStore(CorrectionConnection), true);
            var excelPreview = excel.Preview(f.Target);
            Assert.True(excelPreview.IsSuccess, excelPreview.Message);
            Assert.Equal(5 - coverage, excelPreview.TotalQty);
            var export = excel.Export(f.Target, DateTime.UtcNow);
            Assert.True(export.IsSuccess, export.Message);
            Assert.Equal(0, export.CreatedCodeQty);
            if (coverage < 5) Assert.Equal(5 - coverage, FirstXlsxQuantity(export.FileBytes!));
            Assert.Equal(issuedBefore, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM marking_synthetic_quantity_event e JOIN marking_synthetic_quantity_pool p ON p.id=e.pool_id JOIN marking_order mo ON mo.id=p.marking_order_id WHERE mo.source_order_id IN (@source,@target) AND e.action='ISSUE'", ("source", f.Source), ("target", f.Target)));
            Assert.Equal(0d, await Scalar<double>(db, null!, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
            var refill = FillAdopted(hu, f.Target);
            Assert.True(refill.Success, refill.ErrorMessage);
            Assert.True(refill.PrdAutoClosed);
            Assert.True(FillAdopted(hu, f.Target).AlreadyFilled);
            Assert.Equal(5d, await Scalar<double>(db, null!, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
            Assert.Equal(immutable, await CorrectionSnapshot(db, f, firstDoc));
            Assert.Equal((long)((cycle + 2) * coverage), await Scalar<long>(db, null!, """
SELECT COALESCE(SUM(quantity),0)::bigint FROM marking_hu_reservation r
JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id
WHERE tl.transfer_id=@transfer AND r.state='CONSUMED';
""", ("transfer", f.Transfer)));
        }
    }

    [Theory]
    [InlineData("LegacySynthetic")]
    [InlineData("quantity")]
    public async Task AdoptedCorrection_CallerRollbackRestoresAllFacts_AndRetryUsesSameRequest(string mode)
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, mode, 5);
        Assert.True(FillAdopted(hu, f.Target).Success);
        var request = CorrectionRequest(hu);
        var before = await Scalar<string>(db, null!, "SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id)::text FROM marking_hu_reservation r WHERE transfer_line_id IN (SELECT id FROM order_coverage_transfer_lines WHERE transfer_id=@t)", ("t", f.Transfer));
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(CorrectionConnection).ExecuteInTransaction(store =>
        {
            var cor = new ProductionPalletFillingCorrectionService(store, true, true).Confirm(request);
            Assert.True(cor.Success, cor.Message);
            throw new InvalidOperationException("caller rollback");
        }));
        Assert.Equal(before, await Scalar<string>(db, null!, "SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id)::text FROM marking_hu_reservation r WHERE transfer_line_id IN (SELECT id FROM order_coverage_transfer_lines WHERE transfer_id=@t)", ("t", f.Transfer)));
        Assert.Equal(0L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM marking_hu_correction_revision WHERE transfer_line_id IN (SELECT id FROM order_coverage_transfer_lines WHERE transfer_id=@t)", ("t", f.Transfer)));
        Assert.Equal(5d, await Scalar<double>(db, null!, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
        Assert.True(AdoptedCorrections().Confirm(request).Success);
        Assert.True(FillAdopted(hu, f.Target).Success);
    }

    [Fact]
    public async Task AdoptedCorrection_MixedHu_MultipleSources_PartialCodeAndQuantity_RefillsWholeComposition()
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "RealImport", 3, mixed: true);
        var first = FillAdopted(hu, f.Target);
        Assert.True(first.Success, first.ErrorMessage);
        var source = await CorrectionSnapshot(db, f, first.ClosedPrdDocId!.Value);
        var result = AdoptedCorrections().Confirm(CorrectionRequest(hu));
        Assert.True(result.Success, result.Message);
        Assert.Equal(2L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM marking_hu_correction_revision WHERE adjustment_id=@a", ("a", result.AdjustmentId!.Value)));
        Assert.Equal(2, new PostgresDataStore(CorrectionConnection).GetProductionPalletByHu(hu)!.Lines.Count);
        Assert.Equal(7L, await ReadCustomerCoverage(db, null!, f.Target));
        Assert.True(FillAdopted(hu, f.Target).Success);
        Assert.Equal(source, await CorrectionSnapshot(db, f, first.ClosedPrdDocId.Value));
        Assert.Equal(9d, await Scalar<double>(db, null!, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
    }

    [Theory]
    [InlineData("gtin")]
    [InlineData("source-task")]
    [InlineData("reported")]
    [InlineData("receipt-line")]
    public async Task AdoptedCorrection_ChangedExactProof_BlocksBeforeCorAndClaim(string fault)
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "RealImport", 5);
        Assert.True(FillAdopted(hu, f.Target).Success);
        if (fault == "gtin") await Execute(db, null!, "UPDATE items SET gtin=@g WHERE id=@i", ("g", "0999" + f.Gtin[4..]), ("i", f.Item));
        if (fault == "source-task") await Execute(db, null!, "UPDATE marking_order SET source_order_id=@order WHERE id=@t", ("order", f.Target), ("t", f.Task));
        if (fault == "reported") await Execute(db, null!, "UPDATE marking_code SET reported_at=@at WHERE marking_order_id=@t", ("at", "2026-11-20T12:30:00"), ("t", f.Task));
        if (fault == "receipt-line") await Execute(db, null!, "UPDATE marking_order SET order_line_id=@line WHERE id=@t", ("line", f.TargetLine), ("t", f.Task));
        var request = CorrectionRequest(hu);
        Assert.Contains(AdoptedCorrections().Preview(hu).Blockers, b => b.Code == ProductionPalletFillingCorrectionErrorCodes.AdoptedHuMarkingConsumed);
        Assert.False(AdoptedCorrections().Confirm(request).Success);
        Assert.Equal(0L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE request_id=@r", ("r", Guid.Parse(request.RequestId))));
        Assert.Equal(5d, await Scalar<double>(db, null!, "SELECT SUM(qty_delta) FROM ledger WHERE hu_code=@hu", ("hu", hu)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptedCorrection_FailureAfterRelease_RollsBackRevisionAndBothMarkingRepresentations(bool omitChild)
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "RealImport", 3);
        Assert.True(FillAdopted(hu, f.Target).Success);
        var request = CorrectionRequest(hu);
        var trigger = "cor220_" + Guid.NewGuid().ToString("N");
        await Execute(db, null!, $"""
CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF NEW.correction_revision_id IS NOT NULL THEN {(omitChild ? "RETURN NULL;" : "RAISE EXCEPTION 'fault-after-release';")} END IF;
 RETURN NEW; END $$;
CREATE TRIGGER {trigger} BEFORE INSERT ON marking_hu_reservation FOR EACH ROW EXECUTE FUNCTION {trigger}();
""");
        try
        {
            var error = Assert.Throws<PostgresException>(() => AdoptedCorrections().Confirm(request));
            Assert.Contains(omitChild ? "HU_MARKING_COR_REVISION_INCOMPLETE" : "fault-after-release", error.MessageText);
        }
        finally { await Execute(db, null!, $"DROP TRIGGER {trigger} ON marking_hu_reservation; DROP FUNCTION {trigger}();"); }
        Assert.Equal(3L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM marking_code WHERE receipt_doc_id=@d AND status='Applied'", ("d", f.TargetDoc)));
        Assert.Equal(2L, await Scalar<long>(db, null!, "SELECT COALESCE(SUM(applied_quantity),0)::bigint FROM marking_synthetic_quantity_receipt_balance WHERE receipt_doc_id=@d", ("d", f.TargetDoc)));
        Assert.Equal(0L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE request_id=@r", ("r", Guid.Parse(request.RequestId))));
        Assert.True(AdoptedCorrections().Confirm(request).Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdoptedCorrection_ConcurrentCor_RechecksCommittedRevisionWithoutDoubleRelease(bool sameRequest)
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "quantity", 5);
        Assert.True(FillAdopted(hu, f.Target).Success);
        var request = CorrectionRequest(hu);
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ProductionPalletFillingCorrectionResult? firstResult = null;
        var first = Task.Run(() => new PostgresDataStore(CorrectionConnection).ExecuteInTransaction(store =>
        {
            firstResult = new ProductionPalletFillingCorrectionService(store, true, true).Confirm(request);
            Assert.True(firstResult.Success, firstResult.Message);
            ready.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
        }));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
        var app = "cor220-" + Guid.NewGuid();
        var second = Task.Run(() => new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(CorrectionConnection + ";Application Name=" + app), true, true)
            .Confirm(sameRequest ? request : CorrectionRequest(hu)));
        try { await WaitForCorrectionLock(db, app); Assert.False(second.IsCompleted); }
        finally { release.Set(); }
        await first;
        var secondResult = await second;
        Assert.Equal(sameRequest, secondResult.Success);
        if (sameRequest) Assert.Equal(firstResult!.AdjustmentId, secondResult.AdjustmentId);
        Assert.Equal(1L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM marking_hu_correction_revision WHERE transfer_line_id IN (SELECT id FROM order_coverage_transfer_lines WHERE transfer_id=@t)", ("t", f.Transfer)));
        Assert.Equal(5L, await ReadCustomerCoverage(db, null!, f.Target));
        Assert.True(FillAdopted(hu, f.Target).Success);
    }

    private static async Task WaitForCorrectionLock(NpgsqlConnection db, string app)
    {
        for (var i = 0; i < 250; i++)
        {
            if (await Scalar<long>(db, null!, "SELECT COUNT(*) FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND cardinality(pg_blocking_pids(pid))>0", ("app", app)) > 0) return;
            await Task.Delay(20);
        }
        Assert.Fail("Expected a real PostgreSQL lock wait.");
    }

    [Fact]
    public async Task AdoptedCorrection_RefillRejectsChangedPoolSourceScope_WithoutLosingReservation()
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "quantity", 5);
        Assert.True(FillAdopted(hu, f.Target).Success);
        var cor = AdoptedCorrections().Confirm(CorrectionRequest(hu));
        Assert.True(cor.Success, cor.Message);
        await Execute(db, null!, "UPDATE marking_order SET order_id=NULL WHERE id=@t", ("t", f.Task));
        var error = Assert.Throws<PostgresException>(() => FillAdopted(hu, f.Target));
        Assert.Contains("HU_MARKING_PRD_INVALID_SOURCE_POOL", error.MessageText);
        Assert.Equal(0L, await Scalar<long>(db, null!, "SELECT COUNT(*) FROM ledger WHERE doc_id=@d", ("d", cor.ReplacementPrdDocId!.Value)));
        Assert.Equal(5L, await Scalar<long>(db, null!, "SELECT SUM(quantity)::bigint FROM marking_hu_current_reservation WHERE marking_order_id=@t AND state='ACTIVE'", ("t", f.Task)));
        Assert.NotEqual(ProductionPalletStatus.Filled, new PostgresDataStore(CorrectionConnection).GetProductionPalletByHu(hu)!.Status);
        await Execute(db, null!, "UPDATE marking_order SET order_id=@o WHERE id=@t", ("o", f.Source), ("t", f.Task));
        Assert.True(FillAdopted(hu, f.Target).Success);
    }

    [Fact]
    public async Task AdoptedCorrection_ReturnPlanCancelAndSourceSpendCannotFreeReboundMarking()
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "quantity", 5);
        Assert.True(FillAdopted(hu, f.Target).Success);
        var cor = AdoptedCorrections().Confirm(CorrectionRequest(hu));
        Assert.True(cor.Success, cor.Message);
        Assert.Throws<InvalidOperationException>(() => new OrderService(new PostgresDataStore(CorrectionConnection)).CancelOrder(f.Target));
        Assert.Equal(5L, await ReadCustomerCoverage(db, null!, f.Target));
        await using var tx = await db.BeginTransactionAsync();
        await AssertSqlError(db, tx, "HU_MARKING_CONSUMED_RETURN_PLAN_BLOCKED", """
UPDATE order_coverage_transfers SET compensation_kind='RETURN_PLAN',compensated_at='2026-11-20T12:30:00' WHERE id=@t;
SELECT marking_hu_return_transfer(@t,'TEST');
""", ("t", f.Transfer), ("o", f.Source), ("d", f.SourceDoc), ("p", f.Pallet), ("ol", f.SourceLine), ("dl", f.DocLine));
        var pool = await Scalar<Guid>(db, tx, "SELECT pool_id FROM marking_hu_current_reservation r JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id WHERE tl.transfer_id=@t", ("t", f.Transfer));
        await AssertSqlError(db, tx, "HU_MARKING_CONSUMED_QUANTITY_COR_BLOCKED", """
INSERT INTO marking_synthetic_quantity_event(operation_id,pool_id,action,quantity,receipt_doc_id,receipt_line_id,actor)
VALUES(@op,@pool,'RELEASE',1,@doc,@line,'forged-release');
""", ("op", Guid.NewGuid()), ("pool", pool), ("doc", f.TargetDoc), ("line", f.DocLine));
        await AssertSqlError(db, tx, "HU_MARKING_SOURCE_QUANTITY_RESERVED", "SELECT * FROM marking_synthetic_quantity_apply(@op,@pool,'VOID',1,NULL,NULL,NULL,NULL,'source')", ("op", Guid.NewGuid()), ("pool", pool));
        foreach (var sql in new[] {
            "UPDATE marking_hu_correction_revision SET cor_doc_id=cor_doc_id WHERE adjustment_id=@a",
            "DELETE FROM marking_hu_correction_revision WHERE adjustment_id=@a",
            "TRUNCATE marking_hu_correction_revision" })
            await AssertSqlError(db, tx, "append-only", sql, ("a", cor.AdjustmentId!));
        await tx.RollbackAsync();
        Assert.True(FillAdopted(hu, f.Target).Success);
    }

    [Fact]
    public async Task AdoptedCorrection_SourceConsumerWaitsForCor_ThenCannotSpendReservedRefillQuantity()
    {
        await using var db = await OpenTestDb();
        var (f, hu) = await SeedCorrection(db, "quantity", 5);
        Assert.True(FillAdopted(hu, f.Target).Success);
        var pool = await Scalar<Guid>(db, null!, "SELECT pool_id FROM marking_hu_reservation r JOIN order_coverage_transfer_lines tl ON tl.id=r.transfer_line_id WHERE tl.transfer_id=@t", ("t", f.Transfer));
        var sourceLine = await Scalar<long>(db, null!, """
INSERT INTO doc_lines(doc_id,item_id,order_line_id,qty,to_location_id,to_hu)
SELECT @doc,@item,@line,5,to_location_id,@hu FROM production_pallets WHERE id=@p RETURNING id;
""", ("doc", f.SourceDoc), ("item", f.Item), ("line", f.SourceLine), ("hu", "SOURCE-" + Guid.NewGuid()), ("p", f.Pallet));
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => new PostgresDataStore(CorrectionConnection).ExecuteInTransaction(store =>
        {
            Assert.True(new ProductionPalletFillingCorrectionService(store, true, true).Confirm(CorrectionRequest(hu)).Success);
            ready.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
        }));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
        var app = "cor220-source-" + Guid.NewGuid();
        var second = Task.Run(async () =>
        {
            await using var other = new NpgsqlConnection(CorrectionConnection + ";Application Name=" + app);
            await other.OpenAsync();
            return await Assert.ThrowsAsync<PostgresException>(() => Execute(other, null!,
                "SELECT * FROM marking_synthetic_quantity_apply(@op,@pool,'ASSIGN',5,NULL,NULL,@doc,@line,'source')",
                ("op", Guid.NewGuid()), ("pool", pool), ("doc", f.SourceDoc), ("line", sourceLine)));
        });
        try { await WaitForCorrectionLock(db, app); Assert.False(second.IsCompleted); }
        finally { release.Set(); }
        await first;
        Assert.Contains("HU_MARKING_SOURCE_QUANTITY_RESERVED", (await second).MessageText);
        Assert.Equal(5L, await ReadCustomerCoverage(db, null!, f.Target));
        Assert.True(FillAdopted(hu, f.Target).Success);
    }

    private static async Task<(Fixture Fixture, string Hu)> SeedCorrection(NpgsqlConnection db, string mode, int coverage, bool mixed = false)
    {
        await using var tx = await db.BeginTransactionAsync();
        var f = await Fixture.Create(db, tx, 5);
        if (mixed)
        {
            var second = await Fixture.Create(db, tx, 4);
            await Execute(db, tx, """
UPDATE order_lines SET order_id=@order WHERE id=@line;
UPDATE production_pallets SET status='CANCELLED' WHERE id=@other;
UPDATE production_pallet_lines SET production_pallet_id=@pallet WHERE production_pallet_id=@other;
UPDATE doc_lines SET doc_id=@doc,to_hu=(SELECT hu_code FROM production_pallets WHERE id=@pallet),
 to_location_id=(SELECT to_location_id FROM production_pallets WHERE id=@pallet) WHERE id=@dl;
UPDATE order_coverage_transfers SET target_order_id=@order,target_prd_doc_id=@doc,production_pallet_id=@pallet,
 hu_code=(SELECT hu_code FROM production_pallets WHERE id=@pallet) WHERE id=@transfer;
SELECT * FROM marking_synthetic_quantity_apply(@op,@pool,'ISSUE',4,@task,'COR-MIXED',NULL,NULL,'TEST');
""", ("order", f.Target), ("line", second.TargetLine), ("other", second.Pallet), ("pallet", f.Pallet),
                ("doc", f.TargetDoc), ("dl", second.DocLine), ("transfer", second.Transfer),
                ("op", Guid.NewGuid()), ("pool", Guid.NewGuid()), ("task", second.Task));
            Assert.Equal((0L, 4L), await Reserve(db, tx, second.Transfer));
        }
        await Execute(db, tx, "UPDATE marking_order SET order_line_id=@line WHERE id=@task", ("line", f.SourceLine), ("task", f.Task));
        if (mode == "quantity")
        {
            if (coverage > 0)
                await Execute(db, tx, "SELECT * FROM marking_synthetic_quantity_apply(@op,@pool,'ISSUE',@qty,@task,'COR-TEST',NULL,NULL,'TEST')",
                    ("op", Guid.NewGuid()), ("pool", Guid.NewGuid()), ("qty", (long)coverage), ("task", f.Task));
        }
        else
        {
            if (mode == "RealImport")
                await Execute(db, tx, "UPDATE marking_code_import SET source_type='csv' WHERE id=@id", ("id", f.Import));
            for (var i = 0; i < coverage; i++)
                await InsertCode(db, tx, Guid.NewGuid(), f.Task, f.Import, f.Gtin, mode, "Reserved", mode == "RealImport" ? "REAL-" : "TEMP-CHZ-");
        }
        Assert.Equal((mode == "quantity" ? 0L : coverage, mode == "quantity" ? coverage : 0L), await Reserve(db, tx, f.Transfer));
        if (coverage < 5)
        {
            var task = Guid.NewGuid();
            await Execute(db, tx, """
INSERT INTO marking_order(id,order_id,source_order_id,source_type,order_line_id,item_id,gtin,requested_quantity,request_number,status,created_at,updated_at)
VALUES(@task,@order,@order,'PRODUCTION_ORDER',NULL,@item,@gtin,@qty,@ref,'Printed',@at,@at);
SELECT * FROM marking_synthetic_quantity_apply(@op,@pool,'ISSUE',@qty::bigint,@task,'excel-quantity:v1',NULL,NULL,'TEST');
""", ("task", task), ("order", f.Target), ("line", f.TargetLine), ("item", f.Item), ("gtin", f.Gtin),
                ("qty", 5 - coverage), ("ref", "COR-" + Guid.NewGuid()), ("at", "2026-11-20T12:00:00"),
                ("op", Guid.NewGuid()), ("pool", Guid.NewGuid()));
        }
        await Execute(db, tx, "UPDATE client_blocks SET is_enabled=TRUE WHERE block_key='pc_hu_correction'");
        var hu = await Scalar<string>(db, tx, "SELECT hu_code FROM production_pallets WHERE id=@p", ("p", f.Pallet));
        await tx.CommitAsync();
        return (f, hu);
    }

    private static Task<string> CorrectionSnapshot(NpgsqlConnection db, Fixture f, long firstDoc) => Scalar<string>(db, null!, """
SELECT JSONB_BUILD_OBJECT(
 'transfer',(SELECT to_jsonb(t) FROM order_coverage_transfers t WHERE id=@t),
 'lines',(SELECT jsonb_agg(to_jsonb(tl) ORDER BY id) FROM order_coverage_transfer_lines tl WHERE transfer_id=@t),
 'receipt',(SELECT to_jsonb(d) FROM docs d WHERE id=@d),
 'receipt_lines',(SELECT jsonb_agg(to_jsonb(dl) ORDER BY id) FROM doc_lines dl WHERE doc_id=@d),
 'ledger',(SELECT jsonb_agg(to_jsonb(l) ORDER BY id) FROM ledger l WHERE doc_id=@d),
 'reservations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM marking_hu_reservation r WHERE consumed_doc_id=@d),
 'consume_events',(SELECT jsonb_agg(to_jsonb(e) ORDER BY e.id) FROM marking_hu_reservation_event e
    JOIN marking_hu_reservation r ON r.id=e.reservation_id WHERE r.consumed_doc_id=@d),
 'assign',(SELECT jsonb_agg(to_jsonb(e) ORDER BY id) FROM marking_synthetic_quantity_event e WHERE receipt_doc_id=@d AND action='ASSIGN'))::text;
""", ("t", f.Transfer), ("d", firstDoc));
}
