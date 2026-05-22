namespace FlowStock.Server.Tests.Orders;

public sealed class HuReservationCandidateSqlFilterTests
{
    [Fact]
    public void InternalFilled_ExcludesShippedInternalOrders()
    {
        var sql = ReadSql();

        Assert.Contains("o.status IN (@draft_order_status, @in_progress_order_status)", sql);
    }

    [Fact]
    public void InternalFilled_ExcludesClosedPrd()
    {
        var sql = ReadSql();

        Assert.Contains("d.status = @draft_doc_status", sql);
        Assert.Contains("d.status <> @closed_status", sql);
    }

    [Fact]
    public void InternalFilled_OpenInternalDraftPrdFilled_IsScopedInFilledPalletItems()
    {
        var sql = ReadSql();

        Assert.Contains("filled_pallet_items AS", sql);
        Assert.Contains("pp.status = @filled_status", sql);
        Assert.Contains("o.order_type = @internal_order_type", sql);
    }

    [Fact]
    public void InternalFilled_ExcludesPositiveLedgerStock()
    {
        var sql = ReadSql();

        Assert.Contains("ledger_by_hu_item", sql);
        Assert.Contains("COALESCE(lb.qty, 0) <= @qty_tolerance", sql);
    }

    [Fact]
    public void InternalFilled_NoteIsAlwaysNonEmpty()
    {
        var sql = ReadSql();

        Assert.Contains("'FILLED, PRD не закрыт'::text AS note", sql);
    }

    [Fact]
    public void PostgresDataStore_PassesOpenInternalAndDraftPrdParameters()
    {
        var storeSource = ReadPostgresDataStore();
        var methodStart = storeSource.IndexOf("GetHuReservationCandidateSources", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodSlice = storeSource[methodStart..Math.Min(methodStart + 2800, storeSource.Length)];

        Assert.Contains("@draft_doc_status", methodSlice);
        Assert.Contains("@draft_order_status", methodSlice);
        Assert.Contains("@in_progress_order_status", methodSlice);
    }

    private static string ReadSql() => ReadRepoFile("apps", "windows", "FlowStock.Data", "HuReservationCandidateSql.cs");

    private static string ReadPostgresDataStore() => ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
