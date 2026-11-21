using System.Data;
using System.Globalization;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

/// <summary>
/// Safe read-only diagnostic for #214. Fixtures are rolled back; never use a
/// restored production DB or production connection for this test.
/// Candidate source codes are NOT HU allocations.
/// </summary>
[Collection("Postgres marking integration")]
public sealed class MarkingAdoptedHuCoverageDiagnosticsPostgresTests
{
    [Fact]
    public async Task PrintedLabelWithoutCodes_RemainsUnproven_AndSourceCodesAreOnlyCandidates()
    {
        await using var conn = await OpenDisposableDatabase();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var f = await Fixture.CreateAsync(conn, tx);

        var noCodes = Assert.Single(await ReadDiagnostics(conn, tx, f.TargetOrderId));
        Assert.Equal("NO_SOURCE_TASK", noCodes.Reason);
        Assert.Equal(0L, noCodes.Candidates);
        Assert.Equal(0L, noCodes.ProvenQuantity);
        Assert.Equal(10d, noCodes.TransferredQuantity);

        var task = Guid.NewGuid();
        await Execute(conn, tx, """
INSERT INTO marking_order(
    id, order_id, item_id, gtin, requested_quantity,
    request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES (@task, @source, @item, @gtin, 10, @request, 'Printed',
        'PRODUCTION_ORDER', @source, @at, @at);
""", ("task", task), ("source", f.SourceOrderId), ("item", f.ItemId),
             ("gtin", f.Gtin), ("request", "AUDIT-" + Guid.NewGuid().ToString("N")),
             ("at", "2026-11-18T12:00:00"));

        var import = Guid.NewGuid();
        var code = "TEMP-CHZ-" + Guid.NewGuid().ToString("N");
        await Execute(conn, tx, """
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type,
    matched_marking_order_id, status, created_at)
VALUES(@id, 'audit.xlsx', '<temporary-chz-export>',
       @hash, 'temporary-chz-export', @task, 'Imported', @at);
""", ("id", import), ("hash", "AUDIT-HASH-" + Guid.NewGuid().ToString("N")),
             ("task", task), ("at", "2026-11-18T12:00:00"));
        await Execute(conn, tx, """
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id,
    status, origin, created_at, updated_at)
VALUES (@id, @code, @hash, @gtin, @task, @import,
        'Reserved', 'LegacySynthetic', @at, @at);
""", ("id", Guid.NewGuid()), ("code", code),
             ("hash", "AUDIT-CODE-" + Guid.NewGuid().ToString("N")),
             ("gtin", f.Gtin), ("task", task), ("import", import),
             ("at", "2026-11-18T12:00:00"));

        var withCodes = Assert.Single(await ReadDiagnostics(conn, tx, f.TargetOrderId));
        Assert.Equal("SOURCE_CODES_UNALLOCATED_TO_HU", withCodes.Reason);
        Assert.Equal(1L, withCodes.Candidates);
        Assert.Equal(0L, withCodes.ProvenQuantity);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CompensatedAndStaleTransfer_AreNeverAttributedMarking()
    {
        await using var conn = await OpenDisposableDatabase();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var f = await Fixture.CreateAsync(conn, tx);

        await Execute(conn, tx,
            "UPDATE production_pallets SET status = 'CANCELLED' WHERE id = @pallet;",
            ("pallet", f.PalletId));
        var stale = Assert.Single(await ReadDiagnostics(conn, tx, f.TargetOrderId));
        Assert.Equal("STALE_TRANSFER_IDENTITY", stale.Reason);
        Assert.Equal(0L, stale.ProvenQuantity);

        await Execute(conn, tx,
            "UPDATE order_coverage_transfers SET compensation_kind='RETURN_PLAN', compensated_at=@at WHERE id=@transfer;",
            ("at", "2026-11-18T13:00:00"), ("transfer", f.TransferId));
        var compensated = Assert.Single(await ReadDiagnostics(conn, tx, f.TargetOrderId));
        Assert.Equal("COMPENSATED", compensated.Reason);
        Assert.Equal(0L, compensated.ProvenQuantity);
        await tx.RollbackAsync();
    }

    private sealed record Diagnostic(double TransferredQuantity, long Candidates, long ProvenQuantity, string Reason);

    private static async Task<IReadOnlyList<Diagnostic>> ReadDiagnostics(
        NpgsqlConnection c, NpgsqlTransaction tx, long targetOrderId)
    {
        var sql = await File.ReadAllTextAsync(LocateSql());
        Assert.DoesNotContain("UPDATE ", sql.Split("WITH transfer_lines AS", 2)[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", sql.Split("WITH transfer_lines AS", 2)[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT ", sql.Split("WITH transfer_lines AS", 2)[1], StringComparison.OrdinalIgnoreCase);
        // Only a test-internal Npgsql parameter replaces the psql variable.
        sql = sql.Replace(":'target_order_id'::bigint", "@order_id::bigint", StringComparison.Ordinal);
        await using var cmd = Command(c, tx, sql, ("order_id", targetOrderId));
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Diagnostic>();
        while (await reader.ReadAsync())
            result.Add(new Diagnostic(reader.GetDouble(11), reader.GetInt64(15),
                reader.GetInt64(21), reader.GetString(22)));
        return result;
    }

    private static string LocateSql()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent != null; parent = parent.Parent)
        {
            var candidate = Path.Combine(parent.FullName, "deploy", "scripts", "diagnostics",
                "marking-adoption-coverage.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("marking-adoption-coverage.sql not found");
    }

    private sealed record Fixture(long SourceOrderId, long TargetOrderId, long ItemId, long PalletId, long TransferId, string Gtin)
    {
        public static async Task<Fixture> CreateAsync(NpgsqlConnection c, NpgsqlTransaction tx)
        {
            var token = Guid.NewGuid().ToString("N");
            var gtin = "0460" + Random.Shared.NextInt64(0, 9999999999L).ToString("D10", CultureInfo.InvariantCulture);
            var itemType = await Scalar(c, tx,
                "INSERT INTO item_types(name,code,enable_marking) VALUES(@n,@n,TRUE) RETURNING id;",
                ("n", "AUDIT-" + token));
            var item = await Scalar(c, tx, """
INSERT INTO items(name,barcode,gtin,item_type_id)
VALUES(@n,@n,@gtin,@type) RETURNING id;
""", ("n", "AUDIT-ITEM-" + token), ("gtin", gtin), ("type", itemType));
            var location = await Scalar(c, tx,
                "INSERT INTO locations(code,name) VALUES(@n,@n) RETURNING id;",
                ("n", "AUDIT-LOC-" + token));
            var source = await Scalar(c, tx, """
INSERT INTO orders(order_ref,order_type,status,created_at)
VALUES(@n,'INTERNAL','IN_PROGRESS',@at) RETURNING id;
""", ("n", "AUDIT-SRC-" + token), ("at", "2026-11-18T12:00:00"));
            var target = await Scalar(c, tx, """
INSERT INTO orders(order_ref,order_type,status,created_at)
VALUES(@n,'CUSTOMER','IN_PROGRESS',@at) RETURNING id;
""", ("n", "AUDIT-CUS-" + token), ("at", "2026-11-18T12:00:00"));
            var sourceLine = await Scalar(c, tx, """
INSERT INTO order_lines(order_id,item_id,qty_ordered,production_purpose)
VALUES(@order,@item,10,'INTERNAL_STOCK') RETURNING id;
""", ("order", source), ("item", item));
            var targetLine = await Scalar(c, tx, """
INSERT INTO order_lines(order_id,item_id,qty_ordered,production_purpose)
VALUES(@order,@item,10,'CUSTOMER_ORDER') RETURNING id;
""", ("order", target), ("item", item));
            var targetDoc = await Scalar(c, tx, """
INSERT INTO docs(doc_ref,type,status,order_id,created_at)
VALUES(@ref,'PRODUCTION_RECEIPT','DRAFT',@order,@at) RETURNING id;
""", ("ref", "AUDIT-PRD-" + token), ("order", target),
                 ("at", "2026-11-18T12:00:00"));
            var docLine = await Scalar(c, tx, """
INSERT INTO doc_lines(doc_id,item_id,order_line_id,qty,to_location_id,to_hu)
VALUES(@doc,@item,@line,10,@loc,@hu) RETURNING id;
""", ("doc", targetDoc), ("item", item), ("line", targetLine),
                 ("loc", location), ("hu", "AUDIT-HU-" + token));
            var pallet = await Scalar(c, tx, """
INSERT INTO production_pallets(
    prd_doc_id,doc_line_id,order_id,order_line_id,item_id,
    hu_code,planned_qty,to_location_id,status,created_at)
VALUES(@doc,@line,@order,@order_line,@item,@hu,10,@loc,'PRINTED',@at)
RETURNING id;
""", ("doc", targetDoc), ("line", docLine), ("order", target),
                 ("order_line", targetLine), ("item", item),
                 ("hu", "AUDIT-HU-" + token), ("loc", location),
                 ("at", "2026-11-18T12:00:00"));
            var transfer = await Scalar(c, tx, """
INSERT INTO order_coverage_transfers(
    transfer_type,source_order_id,source_order_ref,source_order_status,
    target_order_id,target_order_ref,source_prd_doc_id,source_prd_doc_ref,
    target_prd_doc_id,target_prd_doc_ref,production_pallet_id,hu_code,
    pallet_status_at_transfer,transferred_qty,created_at)
VALUES('PLANNED_PALLET_ADOPTION',@source,@source_ref,'IN_PROGRESS',
       @target,@target_ref,@source_prd,@source_prd_ref,
       @target_prd,@target_prd_ref,@pallet,@hu,'PRINTED',10,@at)
RETURNING id;
""", ("source", source), ("source_ref", "AUDIT-SRC-" + token),
                 ("target", target), ("target_ref", "AUDIT-CUS-" + token),
                 ("source_prd", 990000000L), ("source_prd_ref", "AUDIT-SOURCE-PRD-" + token),
                 ("target_prd", targetDoc), ("target_prd_ref", "AUDIT-PRD-" + token),
                 ("pallet", pallet), ("hu", "AUDIT-HU-" + token),
                 ("at", "2026-11-18T12:00:00"));
            await Execute(c, tx, """
INSERT INTO order_coverage_transfer_lines(
    transfer_id,source_order_line_id,target_order_line_id,
    source_doc_line_id,item_id,source_qty_ordered_before,
    transferred_qty,source_production_purpose)
VALUES(@transfer,@source_line,@target_line,@doc_line,@item,10,10,'INTERNAL_STOCK');
""", ("transfer", transfer), ("source_line", sourceLine),
                 ("target_line", targetLine), ("doc_line", docLine), ("item", item));
            return new Fixture(source, target, item, pallet, transfer, gtin);
        }
    }

    private static async Task<NpgsqlConnection> OpenDisposableDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION required");
        var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        if (c.Database != "flowstock_test")
        {
            await c.DisposeAsync();
            throw new InvalidOperationException("Tests are restricted to disposable flowstock_test.");
        }
        return c;
    }

    private static async Task<long> Scalar(
        NpgsqlConnection c, NpgsqlTransaction tx, string sql,
        params (string Name, object Value)[] p)
    {
        await using var cmd = Command(c, tx, sql, p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    private static async Task Execute(
        NpgsqlConnection c, NpgsqlTransaction tx, string sql,
        params (string Name, object Value)[] p)
    {
        await using var cmd = Command(c, tx, sql, p);
        await cmd.ExecuteNonQueryAsync();
    }
    private static NpgsqlCommand Command(
        NpgsqlConnection c, NpgsqlTransaction tx, string sql,
        params (string Name, object Value)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in p)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
