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

    [Fact]
    public void PostgresProductionNeed_UsesOwnerAwareHuReservationsForFreeStock()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("reserved_hu_candidates AS", sql);
        Assert.Contains("FROM production_pallets pp", sql);
        Assert.Contains("pp.status = @filled_pallet_status", sql);
        Assert.Contains("FROM order_receipt_plan_lines p", sql);
        Assert.Contains("reserved_hu_ranked AS", sql);
        Assert.Contains("ORDER BY source_priority, source_order_created_at, source_id", sql);
        Assert.Contains("COALESCE(stock.physical_stock_qty, 0) - COALESCE(reserved_stock.reserved_customer_order_qty, 0) AS free_stock_qty", sql);
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
