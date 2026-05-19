namespace FlowStock.Server.Tests.Orders;

public sealed class OrderGetOrdersPageSqlTests
{
    [Fact]
    public void GetOrdersPageSql_DefaultExcludesCancelledAndMergedPersistedStatuses()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("@include_cancelled_merged OR o.status NOT IN (@cancelled_status, @merged_status)", sql, StringComparison.Ordinal);
        Assert.Contains("@include_cancelled_merged", sql, StringComparison.Ordinal);
        Assert.Contains("@cancelled_status", sql, StringComparison.Ordinal);
        Assert.Contains("@merged_status", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOrdersPageSql_UsesSharedStatusSortAndCreatedAtTieBreakers()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("OrderPageSortSql.BuildEffectiveStatusOrderBy(\"eo.effective_status\"", sql, StringComparison.Ordinal);
        Assert.Contains("eo.created_at DESC", sql, StringComparison.Ordinal);
        Assert.Contains("eo.order_ref DESC", sql, StringComparison.Ordinal);
        Assert.Contains("paged_orders.created_at DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql, StringComparison.Ordinal);
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
