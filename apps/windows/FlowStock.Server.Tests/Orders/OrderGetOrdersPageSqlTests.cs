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
        Assert.Contains("UPPER(BTRIM(COALESCE(ob.marking_status, ''))) NOT IN ('PRINTED', 'EXCEL_GENERATED') THEN 'DRAFT'", sql, StringComparison.Ordinal);
        Assert.Contains("UPPER(BTRIM(COALESCE(co.marking_status, ''))) NOT IN ('PRINTED', 'EXCEL_GENERATED') THEN 'DRAFT'", sql, StringComparison.Ordinal);
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
    public void OrderListMarkingRollup_DoesNotCompleteZeroNeedMarkedOrders()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "AND EXISTS (\n               SELECT 1\n               FROM markable_line_need mln\n               WHERE mln.order_id = ob.id\n                 AND mln.qty_ordered > 0\n           )\n           AND NOT EXISTS",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingRollup_CountsOnlyOrderLinkedFreeCodesAsCoverage()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("SELECT COALESCE(mo.order_id, mo.source_order_id) AS order_id", sql, StringComparison.Ordinal);
        Assert.Contains("AND COALESCE(mo.order_id, mo.source_order_id) IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE free.order_id = need.order_id", sql, StringComparison.Ordinal);
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
