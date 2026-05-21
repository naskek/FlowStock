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
