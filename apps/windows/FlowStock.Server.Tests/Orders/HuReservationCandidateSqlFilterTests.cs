namespace FlowStock.Server.Tests.Orders;

public sealed class HuReservationCandidateSqlFilterTests
{
    [Fact]
    public void SelectSources_UsesLedgerStockOnly()
    {
        var sql = ReadSql();

        Assert.Contains("ledger_candidates", sql);
        Assert.Contains("'LEDGER_STOCK'::text AS source", sql);
        Assert.DoesNotContain("internal_candidates", sql);
        Assert.DoesNotContain("INTERNAL_FILLED", sql);
    }

    [Fact]
    public void SelectSources_ExcludesReservedHuForOtherOrders()
    {
        var sql = ReadSql();

        Assert.Contains("reserved_map", sql);
        Assert.Contains("order_receipt_plan_lines", sql);
    }

    [Fact]
    public void PostgresDataStore_PassesLedgerCandidateParameters()
    {
        var storeSource = ReadPostgresDataStore();
        var methodStart = storeSource.IndexOf("GetHuReservationCandidateSources", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodSlice = storeSource[methodStart..Math.Min(methodStart + 2800, storeSource.Length)];

        Assert.Contains("@customer_order_type", methodSlice);
        Assert.Contains("@qty_tolerance", methodSlice);
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
