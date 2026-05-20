namespace FlowStock.Server.Tests.Reports;

public sealed class ProductionNeedSqlTests
{
    [Fact]
    public void PostgresProductionNeed_CapsCustomerNeedByClosedOutboundShipmentRemaining()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("customer_shipped_by_line AS", sql);
        Assert.Contains("LEFT JOIN customer_shipped_by_line shipped ON shipped.order_line_id = col.id", sql);
        Assert.Contains("GREATEST(0, col.qty_ordered - COALESCE(shipped.qty_shipped, 0))", sql);
        Assert.Contains("LEAST(", sql);
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
