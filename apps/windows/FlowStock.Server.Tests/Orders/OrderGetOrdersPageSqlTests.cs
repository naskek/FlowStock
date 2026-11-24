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
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("OrderPageSortSql.BuildEffectiveStatusOrderBy(\"eo.effective_status\"", sql, StringComparison.Ordinal);
        Assert.Contains("OrderPageSortSql.BuildOrderRefDescendingOrderBy(\"eo.order_ref\"", sql, StringComparison.Ordinal);
        Assert.Contains(
            "ORDER BY {effectiveOrderBy},\n{effectiveOrderRefOrderBy},\neo.created_at DESC,\neo.id DESC",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ORDER BY {pagedOrderBy},\n{pagedOrderRefOrderBy},\npaged_orders.created_at DESC,\npaged_orders.id DESC",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListReadModel_InternalDraftStatus_UsesProductionActivity()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("open_production_activity_by_order AS", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) FILTER (WHERE ps.status IN ('PLANNED', 'PRINTED', 'FILLED'))::int AS active_pallet_count", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersApiNoLimit_UsesSameRealOrderSortingAndPendingFirstOnlyBySyntheticRows()
    {
        var program = File.ReadAllText(GetRepoFilePath("apps", "windows", "FlowStock.Server", "Program.cs"));

        Assert.Contains("OrderPageSortSql.SortOrders(orders, includeCancelledMerged)", program, StringComparison.Ordinal);
        Assert.Contains("list.AddRange(GetPendingCreateOrderRows(store, normalized));", program, StringComparison.Ordinal);
        Assert.Contains("is_pending_confirmation = true", program, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderReadModel_DoesNotReadMarkingTablesOrComputeMarkingCoverage()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var start = source.IndexOf("private const string OrderSelectBase", StringComparison.Ordinal);
        var end = source.IndexOf("public PostgresDataStore(", start, StringComparison.Ordinal);
        var sql = source[start..end];
        Assert.DoesNotContain("FROM marking_", sql);
        Assert.DoesNotContain("JOIN marking_", sql);
        Assert.DoesNotContain("markable_line_need", sql);
        Assert.DoesNotContain("marking_rollup", sql);
        var server = File.ReadAllText(GetRepoFilePath("apps", "windows", "FlowStock.Server", "Program.cs"));
        Assert.DoesNotContain("FROM km_code", server);
        Assert.DoesNotContain("if (item.IsMarked)", server);
    }

    private static string GetPostgresDataStorePath()
        => GetRepoFilePath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

    private static string GetRepoFilePath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("PostgresDataStore.cs not found from test output directory.");
    }
}
