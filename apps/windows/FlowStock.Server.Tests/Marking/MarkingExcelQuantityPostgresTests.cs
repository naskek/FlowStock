using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

/// <summary>
/// Append-only quantity journal cannot be test-cleaned by DELETE.
/// Run ONLY against disposable PostgreSQL (CI or explicit local opt-in);
/// do not include this class in a restored-production-copy smoke filter.
/// </summary>
[Collection("Postgres marking integration")]
[Trait("DatabaseScope", "DisposableOnly")]
public sealed class MarkingExcelQuantityPostgresTests
{
    private static readonly DateTime ExportAt = new(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FirstExport_RetryAndGrowth_IssuesOnlyQuantityAndPreservesThreeColumnXlsx()
    {
        var cs = ConnectionString();
        var fixture = await CreateOrderAsync(cs, "INTERNAL", 6000);
        var service = NewService(cs);

        var first = service.Export(fixture.OrderId, ExportAt);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(6000, first.CreatedCodeQty);
        Assert.Equal(6000, first.CoveredQty);
        Assert.Equal(6000, ReadXlsxFirstQuantity(first.FileBytes!));
        Assert.Equal(0, await CountAsync(cs, "marking_code", fixture.OrderId));
        Assert.Equal(0, await CountAsync(cs, "marking_code_import", fixture.OrderId));
        Assert.Equal(1, await CountAsync(cs, "marking_synthetic_quantity_pool", fixture.OrderId));
        Assert.Equal(6000, await IssuedAsync(cs, fixture.OrderId));
        Assert.Equal(1, await CountAsync(cs, "marking_synthetic_quantity_event", fixture.OrderId));

        var retry = service.Export(fixture.OrderId, ExportAt.AddMinutes(1));
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(0, retry.CreatedCodeQty);
        Assert.Equal(6000, retry.CoveredQty);
        Assert.Equal(6000, ReadXlsxFirstQuantity(retry.FileBytes!));
        Assert.Equal(6000, await IssuedAsync(cs, fixture.OrderId));
        Assert.Equal(1, await CountAsync(cs, "marking_synthetic_quantity_event", fixture.OrderId));

        await ExecuteAsync(cs, "UPDATE order_lines SET qty_ordered = 6007 WHERE order_id = @order_id;",
            ("order_id", fixture.OrderId));
        var growth = service.Export(fixture.OrderId, ExportAt.AddMinutes(2));
        Assert.True(growth.IsSuccess, growth.Message);
        Assert.Equal(7, growth.CreatedCodeQty);
        Assert.Equal(6007, growth.CoveredQty);
        Assert.Equal(7, ReadXlsxFirstQuantity(growth.FileBytes!));
        Assert.Equal(6007, await IssuedAsync(cs, fixture.OrderId));
        Assert.Equal(2, await CountAsync(cs, "marking_synthetic_quantity_pool", fixture.OrderId));
        Assert.Equal(2, await CountAsync(cs, "marking_synthetic_quantity_event", fixture.OrderId));
        Assert.Equal(0, await CountAsync(cs, "marking_code", fixture.OrderId));
    }

    [Fact]
    public async Task ParallelOrderExports_SerializeAndCannotDoubleIssue()
    {
        var cs = ConnectionString();
        var fixture = await CreateOrderAsync(cs, "INTERNAL", 900);
        var jobs = new[]
        {
            Task.Run(() => NewService(cs).Export(fixture.OrderId, ExportAt)),
            Task.Run(() => NewService(cs).Export(fixture.OrderId, ExportAt.AddSeconds(1)))
        };
        var results = await Task.WhenAll(jobs);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        Assert.Equal(900, results.Sum(result => result.CreatedCodeQty));
        Assert.Equal(900, await IssuedAsync(cs, fixture.OrderId));
        Assert.Equal(1, await CountAsync(cs, "marking_synthetic_quantity_event", fixture.OrderId));
        Assert.Equal(0, await CountAsync(cs, "marking_code", fixture.OrderId));
    }

    [Fact]
    public async Task InjectedIssueFailure_RollsBackTaskPoolEventAndOrderStatus()
    {
        var cs = ConnectionString();
        var fixture = await CreateOrderAsync(cs, "INTERNAL", 120);
        var suffix = Guid.NewGuid().ToString("N");
        var trigger = "trg_quantity_fail_" + suffix;
        var function = "quantity_fail_" + suffix;
        try
        {
            await ExecuteAsync(cs, $"""
CREATE FUNCTION {function}() RETURNS trigger AS $$
BEGIN
    IF NEW.action = 'ISSUE' AND EXISTS (
        SELECT 1 FROM marking_synthetic_quantity_pool p
        JOIN marking_order mo ON mo.id = p.marking_order_id
        WHERE p.id = NEW.pool_id AND mo.source_order_id = {fixture.OrderId}
    ) THEN
        RAISE EXCEPTION 'quantity issue injected fault';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER {trigger}
BEFORE INSERT ON marking_synthetic_quantity_event
FOR EACH ROW EXECUTE FUNCTION {function}();
""");
            var error = Assert.ThrowsAny<Exception>(
                () => NewService(cs).Export(fixture.OrderId, ExportAt));
            Assert.Contains("quantity issue injected fault", error.ToString());
        }
        finally
        {
            await ExecuteAsync(cs, $"DROP TRIGGER IF EXISTS {trigger} ON marking_synthetic_quantity_event; DROP FUNCTION IF EXISTS {function}();");
        }

        Assert.Equal(0, await CountAsync(cs, "marking_order", fixture.OrderId));
        Assert.Equal(0, await CountAsync(cs, "marking_synthetic_quantity_pool", fixture.OrderId));
        Assert.Equal(0, await CountAsync(cs, "marking_synthetic_quantity_event", fixture.OrderId));
        Assert.Equal("NOT_REQUIRED",
            await ScalarAsync<string>(cs, "SELECT marking_status FROM orders WHERE id = @order_id;",
                ("order_id", fixture.OrderId)));
    }

    [Fact]
    public async Task AdoptedInternalHuWithoutAttributableMarking_BlocksPreviewAndExport()
    {
        var cs = ConnectionString();
        var fixture = await CreateOrderAsync(cs, "CUSTOMER", 80);
        await ExecuteAsync(cs, """
WITH transfer AS (
    INSERT INTO order_coverage_transfers(
        transfer_type, source_order_id, source_order_ref, source_order_status,
        target_order_id, target_order_ref,
        source_prd_doc_id, source_prd_doc_ref,
        target_prd_doc_id, target_prd_doc_ref,
        production_pallet_id, hu_code, pallet_status_at_transfer,
        transferred_qty, created_at)
    VALUES (
        'PLANNED_PALLET_ADOPTION', 98000001, 'SOURCE-INTERNAL', 'IN_PROGRESS',
        @order_id, 'TARGET-CUSTOMER',
        98000002, 'SOURCE-PRD', 98000003, 'TARGET-PRD',
        98000004, 'HU-TEST-ADOPTED', 'PRINTED', 30, '2026-11-18T11:00:00')
    RETURNING id
)
INSERT INTO order_coverage_transfer_lines(
    transfer_id, source_order_line_id, target_order_line_id,
    source_doc_line_id, item_id, source_qty_ordered_before,
    transferred_qty, source_production_purpose)
SELECT id, 98000005, @line_id, 98000006, @item_id, 30, 30, 'CUSTOMER_ORDER'
FROM transfer;
""",
            ("order_id", fixture.OrderId), ("line_id", fixture.LineId), ("item_id", fixture.ItemId));

        var preview = NewService(cs).Preview(fixture.OrderId);
        Assert.False(preview.IsSuccess);
        Assert.Contains("ADOPTED_HU_MARKING_UNVERIFIED", preview.Message);
        var export = NewService(cs).Export(fixture.OrderId, ExportAt);
        Assert.False(export.IsSuccess);
        Assert.Contains("ADOPTED_HU_MARKING_UNVERIFIED", export.Message);
        Assert.Equal(0, await CountAsync(cs, "marking_order", fixture.OrderId));
    }

    [Fact]
    public async Task LegacyGateOff_StillGeneratesPerUnitCodes()
    {
        var cs = ConnectionString();
        var fixture = await CreateOrderAsync(cs, "INTERNAL", 4);
        var legacy = new OrderMarkingExportService(new PostgresDataStore(cs))
            .Export(fixture.OrderId, ExportAt);
        Assert.True(legacy.IsSuccess, legacy.Message);
        Assert.Equal(4, await CountAsync(cs, "marking_code", fixture.OrderId));
        Assert.Equal(0, await CountAsync(cs, "marking_synthetic_quantity_pool", fixture.OrderId));
    }

    private static OrderMarkingExportService NewService(string cs) =>
        new(new PostgresDataStore(cs), quantityIssueEnabled: true);

    private static string ConnectionString()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
        if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("FLOWSTOCK_ALLOW_DISPOSABLE_MARKING_TESTS") != "1")
            throw new InvalidOperationException(
                "Quantity integration tests write immutable audit; run only on a disposable database. " +
                "Explicitly opt in via FLOWSTOCK_ALLOW_DISPOSABLE_MARKING_TESTS=1.");
        return cs.Trim();
    }

    private static async Task<(long OrderId, long ItemId, long LineId)> CreateOrderAsync(
        string cs, string type, int quantity)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var gtin = $"046{new string(suffix.Where(char.IsDigit).DefaultIfEmpty('7').ToArray())}00000000000000"[..14];
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        var typeId = await InsertIdAsync(conn,
            "INSERT INTO item_types(name,code,enable_marking) VALUES(@n,@c,TRUE) RETURNING id;",
            ("n", "Quantity test " + suffix), ("c", "QTY_" + suffix));
        var itemId = await InsertIdAsync(conn,
            "INSERT INTO items(name,barcode,gtin,base_uom,item_type_id) VALUES(@n,@b,@g,'шт',@t) RETURNING id;",
            ("n", "Quantity item " + suffix), ("b", "QTY-" + suffix),
            ("g", gtin), ("t", typeId));
        var orderId = await InsertIdAsync(conn,
            "INSERT INTO orders(order_ref,order_type,status,created_at,marking_status) VALUES(@r,@t,'DRAFT','2026-11-18T11:00:00','NOT_REQUIRED') RETURNING id;",
            ("r", "QTY-" + suffix), ("t", type));
        var lineId = await InsertIdAsync(conn,
            "INSERT INTO order_lines(order_id,item_id,qty_ordered) VALUES(@o,@i,@q) RETURNING id;",
            ("o", orderId), ("i", itemId), ("q", (double)quantity));
        return (orderId, itemId, lineId);
    }

    private static async Task<long> InsertIdAsync(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        string cs, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        string cs, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static Task<long> IssuedAsync(string cs, long orderId) =>
        ScalarAsync<long>(cs, """
SELECT COALESCE(SUM(b.issued_quantity - b.voided_quantity), 0)::bigint
FROM marking_synthetic_quantity_balance b
JOIN marking_order mo ON mo.id = b.marking_order_id
WHERE mo.source_order_id = @order_id;
""", ("order_id", orderId));

    private static Task<long> CountAsync(string cs, string table, long orderId)
    {
        var sql = table switch
        {
            "marking_order" =>
                "SELECT COUNT(*) FROM marking_order WHERE source_order_id = @order_id;",
            "marking_code" =>
                "SELECT COUNT(*) FROM marking_code c JOIN marking_order mo ON mo.id=c.marking_order_id WHERE mo.source_order_id=@order_id;",
            "marking_code_import" =>
                "SELECT COUNT(*) FROM marking_code_import i JOIN marking_order mo ON mo.id=i.matched_marking_order_id WHERE mo.source_order_id=@order_id;",
            "marking_synthetic_quantity_pool" =>
                "SELECT COUNT(*) FROM marking_synthetic_quantity_pool p JOIN marking_order mo ON mo.id=p.marking_order_id WHERE mo.source_order_id=@order_id;",
            "marking_synthetic_quantity_event" =>
                "SELECT COUNT(*) FROM marking_synthetic_quantity_event e JOIN marking_synthetic_quantity_pool p ON p.id=e.pool_id JOIN marking_order mo ON mo.id=p.marking_order_id WHERE mo.source_order_id=@order_id;",
            _ => throw new ArgumentException("Unsupported table", nameof(table))
        };
        return ScalarAsync<long>(cs, sql, ("order_id", orderId));
    }

    private static double ReadXlsxFirstQuantity(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = document.Descendants(ns + "c").ToArray();
        Assert.Equal(new[] { "A1", "B1", "C1" },
            cells.Select(cell => (string?)cell.Attribute("r")));
        var quantity = cells.Single(cell => (string?)cell.Attribute("r") == "C1")
            .Element(ns + "v")?.Value;
        return double.Parse(quantity!, CultureInfo.InvariantCulture);
    }
}
