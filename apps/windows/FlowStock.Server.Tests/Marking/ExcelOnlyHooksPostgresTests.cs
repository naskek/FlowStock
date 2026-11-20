using FlowStock.Server.Tests.Support;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection(PostgresLocationIntegrationTestCollection.Name)]
public sealed class ExcelOnlyHooksPostgresTests
{
    [Fact]
    public async Task Cutover_DisablesOnlyHistoricalHooks_AndPreservesPhysicalWrites()
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var hooks = new[] {
            ("items", "trg_items_marking_subject_gtin_guard", "guard_marking_subject_item_gtin_mutation", "UPDATE OF gtin"),
            ("production_pallet_lines", "trg_production_pallet_line_marking_subject", "sync_marking_production_subject", "INSERT OR UPDATE OR DELETE"),
            ("production_pallets", "trg_production_pallet_marking_subject_lifecycle", "sync_marking_subject_pallet_lifecycle", "UPDATE OF status")
        };
        await Execute(connection, transaction, "CREATE TEMP TABLE excel_only_hook_probe(source text) ON COMMIT DROP;");
        foreach (var (table, trigger, function, events) in hooks)
            await Execute(connection, transaction, $"""
CREATE OR REPLACE FUNCTION public.{function}() RETURNS trigger LANGUAGE plpgsql AS $probe$
BEGIN
    INSERT INTO pg_temp.excel_only_hook_probe VALUES ('marking');
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$probe$;
DROP TRIGGER IF EXISTS {trigger} ON public.{table};
CREATE TRIGGER {trigger} BEFORE {events} ON public.{table}
FOR EACH ROW EXECUTE FUNCTION public.{function}();
""");
        await Execute(connection, transaction, $"""
CREATE FUNCTION public.excel_only_physical_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $probe$
BEGIN INSERT INTO pg_temp.excel_only_hook_probe VALUES ('physical'); RETURN NEW; END;
$probe$;
CREATE TRIGGER excel_only_physical_{suffix} BEFORE UPDATE ON items
FOR EACH ROW EXECUTE FUNCTION public.excel_only_physical_{suffix}();
INSERT INTO items(name, barcode, gtin, base_uom, is_active)
VALUES ('excel-only-{suffix}', 'excel-only-{suffix}', '99110000000001', 'шт', TRUE);
UPDATE items SET gtin = '99110000000002' WHERE barcode = 'excel-only-{suffix}';
""");
        Assert.Equal(1L, await Scalar(connection, transaction, "SELECT count(*) FROM excel_only_hook_probe WHERE source='marking'"));
        await Execute(connection, transaction, Migration());
        await Execute(connection, transaction, Migration());
        Assert.Equal(3L, await Scalar(connection, transaction, """
SELECT count(*) FROM pg_trigger WHERE tgname IN (
 'trg_items_marking_subject_gtin_guard',
 'trg_production_pallet_line_marking_subject',
 'trg_production_pallet_marking_subject_lifecycle') AND tgenabled='D';
"""));
        await Execute(connection, transaction, $"UPDATE items SET gtin='99110000000003' WHERE barcode='excel-only-{suffix}';");
        Assert.Equal(1L, await Scalar(connection, transaction, "SELECT count(*) FROM excel_only_hook_probe WHERE source='marking'"));
        Assert.Equal(2L, await Scalar(connection, transaction, "SELECT count(*) FROM excel_only_hook_probe WHERE source='physical'"));
        Assert.Equal(1L, await Scalar(connection, transaction, $"SELECT count(*) FROM items WHERE barcode='excel-only-{suffix}' AND gtin='99110000000003'"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Cutover_RejectsUnexpectedFunctionInsteadOfDisablingAnUnknownGuard()
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var suffix = Guid.NewGuid().ToString("N");
        await Execute(connection, transaction, $"""
CREATE FUNCTION public.excel_only_unknown_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $probe$
BEGIN RETURN NEW; END; $probe$;
DROP TRIGGER IF EXISTS trg_items_marking_subject_gtin_guard ON public.items;
CREATE TRIGGER trg_items_marking_subject_gtin_guard BEFORE UPDATE ON public.items
FOR EACH ROW EXECUTE FUNCTION public.excel_only_unknown_{suffix}();
""");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, transaction, Migration()));
        Assert.Contains("Unexpected function for marking hook", error.MessageText);
        await transaction.RollbackAsync();
    }

    private static string ConnectionString() => Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")
        ?? throw new InvalidOperationException("PostgreSQL test connection is required.");

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string Migration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, "deploy", "postgres", "migrations", "V0059__disable_operational_marking_hooks.sql");
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Excel-only hook migration not found.");
    }
}
