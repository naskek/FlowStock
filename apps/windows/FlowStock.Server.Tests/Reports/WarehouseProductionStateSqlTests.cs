namespace FlowStock.Server.Tests.Reports;

public sealed class WarehouseProductionStateSqlTests
{
    [Fact]
    public void PostgresWarehouseProductionState_UsesBatchOrderReadModels()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("GetWarehouseProductionStateCustomerOrdersByItem", sql);
        Assert.Contains("customer_order_lines AS", sql);
        Assert.Contains("shipped_by_line AS", sql);
        Assert.Contains("GetWarehouseProductionStateInternalOrdersByItem", sql);
        Assert.Contains("line_remaining AS", sql);
        Assert.Contains("GetWarehouseProductionStatePalletsByItem", sql);
        Assert.Contains("pallet_rows AS", sql);
    }

    [Fact]
    public void PostgresWarehouseProductionState_PalletPlanUsesEffectiveCustomerOrderPredicate()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("LEFT JOIN orders o ON o.id = COALESCE(pp.order_id, d.order_id)", sql);
        Assert.Contains("o.order_type = @customer_order_type", sql);
        Assert.Contains("o.status NOT IN (@shipped_order_status, @cancelled_order_status, @merged_order_status)", sql);
        Assert.Contains("o.order_type = @internal_order_type", sql);
        Assert.Contains("o.status IN (@draft_order_status, @in_progress_order_status)", sql);
        Assert.DoesNotContain("WITH work_docs AS", sql);
        Assert.DoesNotContain("INNER JOIN production_pallets pp ON pp.prd_doc_id = wd.id", sql);
    }

    private static string GetPostgresDataStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("PostgresDataStore.cs not found from test output directory.");
    }
}
