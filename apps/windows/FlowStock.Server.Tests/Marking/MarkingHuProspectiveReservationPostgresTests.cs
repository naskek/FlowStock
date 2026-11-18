using System.Data;
using System.Globalization;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

/// <summary>
/// All writes occur inside an uncommitted test transaction on the disposable
/// PostgreSQL CI database. No production backup or historical transfer is
/// modified. This covers only the inactive V0054 reservation primitive.
/// </summary>
[Collection("Postgres marking integration")]
[Trait("DatabaseScope", "DisposableOnly")]
public sealed class MarkingHuProspectiveReservationPostgresTests
{
    [Fact]
    public async Task PrintedHuWithoutSourceCode_HasZeroVerifiedReservation()
    {
        await using var db = await OpenTestDb();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var f = await Fixture.Create(db, tx, qty: 9);
        var result = await Reserve(db, tx, f.Transfer);
        Assert.Equal((0L, 0L), result);
        Assert.Equal(0, await Count(db, tx, f.Transfer));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task HistoricalTransferCannotBeRetrospectivelyAttributed()
    {
        await using var db = await OpenTestDb();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var f = await Fixture.Create(db, tx, qty: 4, prospective: false);
        await CreateSourceCodes(db, tx, f, 4);
        await AssertSqlError(db, tx, "HU_MARKING_NOT_PROSPECTIVE",
            "SELECT * FROM marking_hu_reserve_transfer(@transfer,'TEST');",
            ("transfer", f.Transfer));
        await AssertSqlError(db, tx, "HU_MARKING_RETROACTIVE_ALLOCATION_FORBIDDEN",
            "UPDATE order_coverage_transfers SET hu_marking_allocation_version='v1' WHERE id=@transfer;",
            ("transfer", f.Transfer));
        Assert.Equal(0, await Count(db, tx, f.Transfer));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task VerifiedPerCode_ReservesOnlySourceFreeCodes_AndGuardsUntilReturnPlan()
    {
        await using var db = await OpenTestDb();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var f = await Fixture.Create(db, tx, qty: 9);
        var codes = await CreateSourceCodes(db, tx, f, 6);
        var unrelated = Guid.NewGuid();
        await InsertCode(db, tx, unrelated, f.Task, f.Import, "09999123456789",
            "LegacySynthetic", "Reserved", "TEMP-CHZ-");
        var result = await Reserve(db, tx, f.Transfer);
        Assert.Equal((6L, 0L), result);
        Assert.Equal(result, await Reserve(db, tx, f.Transfer));
        Assert.Equal(6, await Count(db, tx, f.Transfer));
        Assert.Equal(6, await CountEvents(db, tx, f.Transfer, "RESERVE"));

        await AssertSqlError(db, tx, "HU_MARKING_SOURCE_CODE_RESERVED",
            "UPDATE marking_code SET status='Applied' WHERE id=@id;",
            ("id", codes[0]));
        await AssertSqlError(db, tx, "HU_MARKING_SOURCE_CODE_RESERVED",
            "DELETE FROM marking_code WHERE id=@id;", ("id", codes[0]));
        // Source code with a mismatched GTIN never contributes.
        Assert.Equal("Reserved", await Scalar<string>(db, tx,
            "SELECT status FROM marking_code WHERE id=@id", ("id", unrelated)));

        await Return(db, tx, f);
        Assert.Equal(6, await CountEvents(db, tx, f.Transfer, "RETURN_PLAN"));
        Assert.Equal(0, await Count(db, tx, f.Transfer));
        await Execute(db, tx, "UPDATE marking_code SET status='Printed' WHERE id=@id;",
            ("id", codes[0]));
        Assert.Equal(6, await Scalar<long>(db, tx,
            "SELECT COUNT(*) FROM marking_hu_reservation r JOIN order_coverage_transfer_lines l ON l.id=r.transfer_line_id WHERE l.transfer_id=@transfer AND r.state='RELEASED';",
            ("transfer", f.Transfer)));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task QuantityPool_ReservesSourceBalanceAndBlocksSourceDebit()
    {
        await using var db = await OpenTestDb();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var f = await Fixture.Create(db, tx, qty: 12);
        var pool = Guid.NewGuid();
        await Execute(db, tx, """
SELECT * FROM marking_synthetic_quantity_apply(
    @operation, @pool, 'ISSUE', 8, @task, 'TEST-218', NULL::bigint,
    NULL::bigint, 'TEST');
""", ("operation", Guid.NewGuid()), ("pool", pool), ("task", f.Task));
        Assert.Equal((0L, 8L), await Reserve(db, tx, f.Transfer));
        Assert.Equal((0L, 8L), await Reserve(db, tx, f.Transfer));
        await AssertSqlError(db, tx, "HU_MARKING_SOURCE_QUANTITY_RESERVED", """
SELECT * FROM marking_synthetic_quantity_apply(
    @operation, @pool, 'VOID', 1, NULL::uuid, NULL::text, NULL::bigint,
    NULL::bigint, 'TEST');
""", ("operation", Guid.NewGuid()), ("pool", pool));
        Assert.Equal(8L, await Scalar<long>(db, tx,
            "SELECT available_quantity FROM marking_synthetic_quantity_balance WHERE pool_id=@pool;",
            ("pool", pool)));
        await Return(db, tx, f);
        await Execute(db, tx, """
SELECT * FROM marking_synthetic_quantity_apply(
    @operation, @pool, 'VOID', 1, NULL::uuid, NULL::text, NULL::bigint,
    NULL::bigint, 'TEST');
""", ("operation", Guid.NewGuid()), ("pool", pool));
        Assert.Equal(7L, await Scalar<long>(db, tx,
            "SELECT available_quantity FROM marking_synthetic_quantity_balance WHERE pool_id=@pool;",
            ("pool", pool)));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task SourceTaskWithPerCodeAndQuantity_IsRejectedWithFullRollback()
    {
        await using var db = await OpenTestDb();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var f = await Fixture.Create(db, tx, qty: 4);
        // The normal writer rejects per-code -> quantity mixing. Model only
        // an existing ambiguous history by issuing a pool first, then
        // injecting per-code facts on the disposable database.
        await Execute(db, tx, """
SELECT * FROM marking_synthetic_quantity_apply(
    @operation, @pool, 'ISSUE', 4, @task, 'TEST-218', NULL::bigint,
    NULL::bigint, 'TEST');
""", ("operation", Guid.NewGuid()), ("pool", Guid.NewGuid()), ("task", f.Task));
        await CreateSourceCodes(db, tx, f, 2);
        await AssertSqlError(db, tx, "HU_MARKING_MIXED_TASK",
            "SELECT * FROM marking_hu_reserve_transfer(@transfer,'TEST');",
            ("transfer", f.Transfer));
        Assert.Equal(0, await Count(db, tx, f.Transfer));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task SourceMarkedPartialAllocation_RemainsPartialAndSavepointRollbackRemovesAllNewReservations()
    {
        await using var db = await OpenTestDb();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var f = await Fixture.Create(db, tx, qty: 7);
        await CreateSourceCodes(db, tx, f, 3);
        await Execute(db, tx, "SAVEPOINT before_reservation;");
        Assert.Equal((3L, 0L), await Reserve(db, tx, f.Transfer));
        await Execute(db, tx, "ROLLBACK TO SAVEPOINT before_reservation;");
        Assert.Equal(0, await Count(db, tx, f.Transfer));
        Assert.Equal(0, await CountEvents(db, tx, f.Transfer, "RESERVE"));
        Assert.Equal((3L, 0L), await Reserve(db, tx, f.Transfer));
        await tx.RollbackAsync();
    }

    private sealed record Fixture(long Source, long Target, long TargetDoc, long SourceDoc,
        long SourceLine, long TargetLine, long DocLine, long Pallet, long Transfer,
        long Item, string Gtin, Guid Task, Guid Import)
    {
        public static async Task<Fixture> Create(
            NpgsqlConnection c, NpgsqlTransaction t, int qty, bool prospective = true)
        {
            var token = Guid.NewGuid().ToString("N");
            var gtin = "0460" + (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0)
                % 10000000000UL).ToString("D10", CultureInfo.InvariantCulture);
            var type = await Scalar<long>(c, t,
                "INSERT INTO item_types(name,code,enable_marking) VALUES(@n,@n,TRUE) RETURNING id;",
                ("n","HU-RES-TYPE-"+token));
            var item = await Scalar<long>(c, t,
                "INSERT INTO items(name,barcode,item_type_id,gtin) VALUES(@n,@n,@type,@gtin) RETURNING id;",
                ("n","HU-RES-ITEM-"+token),("type",type),("gtin",gtin));
            var loc = await Scalar<long>(c, t,
                "INSERT INTO locations(code,name) VALUES(@n,@n) RETURNING id;",
                ("n","HU-RES-LOC-"+token));
            var source = await Scalar<long>(c,t,"""
INSERT INTO orders(order_ref,order_type,status,created_at)
VALUES(@n,'INTERNAL','IN_PROGRESS',@at) RETURNING id;
""",("n","HU-RES-SRC-"+token),("at","2026-11-18T12:00:00"));
            var target = await Scalar<long>(c,t,"""
INSERT INTO orders(order_ref,order_type,status,created_at)
VALUES(@n,'CUSTOMER','IN_PROGRESS',@at) RETURNING id;
""",("n","HU-RES-TGT-"+token),("at","2026-11-18T12:00:00"));
            var sourceLine = await Scalar<long>(c,t,"""
INSERT INTO order_lines(order_id,item_id,qty_ordered,production_purpose)
VALUES(@o,@i,@q,'INTERNAL_STOCK') RETURNING id;
""",("o",source),("i",item),("q",(double)qty));
            var targetLine = await Scalar<long>(c,t,"""
INSERT INTO order_lines(order_id,item_id,qty_ordered,production_purpose)
VALUES(@o,@i,@q,'CUSTOMER_ORDER') RETURNING id;
""",("o",target),("i",item),("q",(double)qty));
            var sourceDoc = await Scalar<long>(c,t,"""
INSERT INTO docs(doc_ref,type,status,order_id,created_at)
VALUES(@n,'PRODUCTION_RECEIPT','DRAFT',@o,@at) RETURNING id;
""",("n","HU-RES-SDOC-"+token),("o",source),("at","2026-11-18T12:00:00"));
            var targetDoc = await Scalar<long>(c,t,"""
INSERT INTO docs(doc_ref,type,status,order_id,created_at)
VALUES(@n,'PRODUCTION_RECEIPT','DRAFT',@o,@at) RETURNING id;
""",("n","HU-RES-TDOC-"+token),("o",target),("at","2026-11-18T12:00:00"));
            var hu = "HU-RES-"+token;
            var docLine = await Scalar<long>(c,t,"""
INSERT INTO doc_lines(doc_id,item_id,order_line_id,qty,to_location_id,to_hu)
VALUES(@d,@i,@l,@q,@loc,@hu) RETURNING id;
""",("d",targetDoc),("i",item),("l",targetLine),("q",(double)qty),
                ("loc",loc),("hu",hu));
            var pallet = await Scalar<long>(c,t,"""
INSERT INTO production_pallets(
    prd_doc_id,doc_line_id,order_id,order_line_id,item_id,
    hu_code,planned_qty,to_location_id,status,created_at)
VALUES(@d,@dl,@o,@l,@i,@hu,@q,@loc,'PRINTED',@at) RETURNING id;
""",("d",targetDoc),("dl",docLine),("o",target),("l",targetLine),
                ("i",item),("hu",hu),("q",(double)qty),("loc",loc),
                ("at","2026-11-18T12:00:00"));
            await Execute(c,t,"""
INSERT INTO production_pallet_lines(
    production_pallet_id,doc_line_id,order_line_id,item_id,
    planned_qty,filled_qty,created_at)
VALUES(@p,@dl,@l,@i,@q,0,@at);
""",("p",pallet),("dl",docLine),("l",targetLine),("i",item),
                ("q",(double)qty),("at","2026-11-18T12:00:00"));
            var transfer = await Scalar<long>(c,t,"""
INSERT INTO order_coverage_transfers(
    transfer_type,source_order_id,source_order_ref,source_order_status,
    target_order_id,target_order_ref,source_prd_doc_id,source_prd_doc_ref,
    target_prd_doc_id,target_prd_doc_ref,production_pallet_id,hu_code,
    pallet_status_at_transfer,transferred_qty,created_at,
    hu_marking_allocation_version)
VALUES('PLANNED_PALLET_ADOPTION',@s,@sr,'IN_PROGRESS',
       @t,@tr,@sd,@sdr,@td,@tdr,@p,@hu,'PRINTED',@q,@at,@v)
RETURNING id;
""",("s",source),("sr","HU-RES-SRC-"+token),
                ("t",target),("tr","HU-RES-TGT-"+token),("sd",sourceDoc),
                ("sdr","HU-RES-SDOC-"+token),("td",targetDoc),
                ("tdr","HU-RES-TDOC-"+token),("p",pallet),("hu",hu),
                ("q",(double)qty),("at","2026-11-18T12:00:00"),
                ("v",prospective?(object)"v1":DBNull.Value));
            await Execute(c,t,"""
INSERT INTO order_coverage_transfer_lines(
    transfer_id,source_order_line_id,target_order_line_id,
    source_doc_line_id,item_id,source_qty_ordered_before,
    transferred_qty,source_production_purpose)
VALUES(@transfer,@sl,@tl,@dl,@i,@q,@q,'INTERNAL_STOCK');
""",("transfer",transfer),("sl",sourceLine),("tl",targetLine),
                ("dl",docLine),("i",item),("q",(double)qty));
            var task = Guid.NewGuid();
            await Execute(c,t,"""
INSERT INTO marking_order(
    id, order_id, item_id, gtin, requested_quantity, request_number,
    status, source_type, source_order_id, created_at, updated_at)
VALUES(@task,@o,@i,@gtin,@q,@ref,'Printed','PRODUCTION_ORDER',@o,@at,@at);
""",("task",task),("o",source),("i",item),("gtin",gtin),("q",qty),
                ("ref","HU-RES-"+token),("at","2026-11-18T12:00:00"));
            var import=Guid.NewGuid();
            await Execute(c,t,"""
INSERT INTO marking_code_import(
    id,original_filename,storage_path,file_hash,source_type,
    matched_marking_order_id,status,created_at)
VALUES(@id,'temporary.xlsx','<temporary-chz-export>',@hash,
       'temporary-chz-export',@task,'Imported',@at);
""",("id",import),("hash","HU-RES-HASH-"+token),
                ("task",task),("at","2026-11-18T12:00:00"));
            return new Fixture(source,target,targetDoc,sourceDoc,sourceLine,targetLine,
                docLine,pallet,transfer,item,gtin,task,import);
        }
    }

    private static async Task<List<Guid>> CreateSourceCodes(
        NpgsqlConnection c, NpgsqlTransaction t, Fixture f, int amount)
    {
        var codes = new List<Guid>();
        for(var i=0;i<amount;i++)
        {
            var id=Guid.NewGuid();
            codes.Add(id);
            await InsertCode(c,t,id,f.Task,f.Import,f.Gtin,
                "LegacySynthetic","Reserved","TEMP-CHZ-");
        }
        return codes;
    }

    private static Task InsertCode(NpgsqlConnection c, NpgsqlTransaction t,
        Guid id, Guid task, Guid import, string gtin, string origin,
        string status, string prefix) =>
        Execute(c,t,"""
INSERT INTO marking_code(
    id,code,code_hash,gtin,marking_order_id,import_id,status,
    origin,created_at,updated_at)
VALUES(@id,@code,@hash,@gtin,@task,@imp,@status,@origin,@at,@at);
""",("id",id),("code",prefix+id.ToString("N")),
            ("hash","HU-RES-CODE-HASH-"+id.ToString("N")),
            ("gtin",gtin),("task",task),("imp",import),
            ("status",status),("origin",origin),("at","2026-11-18T12:00:00"));

    private static async Task<(long Codes,long Quantity)> Reserve(
        NpgsqlConnection db,NpgsqlTransaction tx,long transfer)
    {
        await using var cmd=Command(db,tx,
            "SELECT * FROM marking_hu_reserve_transfer(@transfer,'TEST');",
            ("transfer",transfer));
        await using var reader=await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0),reader.GetInt64(1));
    }

    private static async Task Return(NpgsqlConnection db,NpgsqlTransaction tx,Fixture f)
    {
        await Execute(db,tx,
            "UPDATE production_pallets SET order_id=@source,prd_doc_id=@doc WHERE id=@pallet;",
            ("source",f.Source),("doc",f.SourceDoc),("pallet",f.Pallet));
        await Execute(db,tx,
            "UPDATE production_pallet_lines SET order_line_id=@line WHERE production_pallet_id=@pallet;",
            ("line",f.SourceLine),("pallet",f.Pallet));
        await Execute(db,tx,
            "UPDATE doc_lines SET doc_id=@doc,order_line_id=@line WHERE id=@dl;",
            ("doc",f.SourceDoc),("line",f.SourceLine),("dl",f.DocLine));
        await Execute(db,tx,
            "UPDATE order_coverage_transfers SET compensation_kind='RETURN_PLAN',compensated_at=@at WHERE id=@transfer;",
            ("at","2026-11-18T12:05:00"),("transfer",f.Transfer));
        Assert.True(await Scalar<long>(db,tx,
            "SELECT marking_hu_return_transfer(@transfer,'TEST');",
            ("transfer",f.Transfer)) >= 0);
    }

    private static async Task AssertSqlError(
        NpgsqlConnection db,NpgsqlTransaction tx,string message,string sql,
        params (string Name,object Value)[] args)
    {
        var name="hu_res_"+Guid.NewGuid().ToString("N");
        await Execute(db,tx,"SAVEPOINT "+name+";");
        var error=await Assert.ThrowsAsync<PostgresException>(async()=>
            await Execute(db,tx,sql,args));
        Assert.Contains(message,error.MessageText);
        await Execute(db,tx,"ROLLBACK TO SAVEPOINT "+name+";");
        await Execute(db,tx,"RELEASE SAVEPOINT "+name+";");
    }

    private static async Task<int> Count(NpgsqlConnection c,NpgsqlTransaction t,long transfer) =>
        (int)await Scalar<long>(c,t,"""
SELECT COUNT(*)::bigint FROM marking_hu_reservation r
JOIN order_coverage_transfer_lines l ON l.id=r.transfer_line_id
WHERE l.transfer_id=@transfer AND r.state='ACTIVE';
""",("transfer",transfer));

    private static async Task<int> CountEvents(NpgsqlConnection c,NpgsqlTransaction t,
        long transfer,string action) => (int)await Scalar<long>(c,t,"""
SELECT COUNT(*)::bigint FROM marking_hu_reservation_event e
JOIN marking_hu_reservation r ON r.id=e.reservation_id
JOIN order_coverage_transfer_lines l ON l.id=r.transfer_line_id
WHERE l.transfer_id=@transfer AND e.action=@action;
""",("transfer",transfer),("action",action));

    private static async Task<NpgsqlConnection> OpenTestDb()
    {
        var cs=Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if(string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Disposable PostgreSQL test database required.");
        var db=new NpgsqlConnection(cs);
        await db.OpenAsync();
        if(db.Database!="flowstock_test")
        {
            await db.DisposeAsync();
            throw new InvalidOperationException("Prospective HU tests restricted to flowstock_test.");
        }
        return db;
    }

    private static async Task<T> Scalar<T>(
        NpgsqlConnection c,NpgsqlTransaction t,string sql,
        params (string Name,object Value)[] args)
    {
        await using var cmd=Command(c,t,sql,args);
        var val=await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(val!,typeof(T),CultureInfo.InvariantCulture);
    }
    private static async Task Execute(
        NpgsqlConnection c,NpgsqlTransaction t,string sql,
        params (string Name,object Value)[] args)
    {
        await using var cmd=Command(c,t,sql,args);
        await cmd.ExecuteNonQueryAsync();
    }
    private static NpgsqlCommand Command(
        NpgsqlConnection c,NpgsqlTransaction t,string sql,
        params (string Name,object Value)[] args)
    {
        var cmd=c.CreateCommand();
        cmd.Transaction=t;
        cmd.CommandText=sql;
        foreach(var (name,value) in args)
            cmd.Parameters.AddWithValue(name,value);
        return cmd;
    }
}
