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
            "COALESCE(mof.marking_applies, FALSE)\n           AND COALESCE(mof.has_ordered_markable_qty, FALSE)\n           AND NOT COALESCE(mcb.has_uncovered_positive_need, FALSE) AS marking_completed",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingRollup_CountsOnlyOrderLinkedFreeCodesAsCoverage()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("selected_marking_orders AS", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(mo.order_id, mo.source_order_id) AS order_id", sql, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN order_scope os ON os.id = COALESCE(mo.order_id, mo.source_order_id)", sql, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN selected_marking_orders smo ON smo.order_id = need.order_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCodeAggregates_AreScopedToSelectedOrdersBeforeTouchingMarkingCode()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var selectedStart = sql.IndexOf("selected_marking_orders AS", StringComparison.Ordinal);
        var freeStart = sql.IndexOf("free_marking_need_coverage AS", StringComparison.Ordinal);
        var boundStart = sql.IndexOf("bound_marking_need_coverage AS", StringComparison.Ordinal);
        var freeSection = sql[freeStart..boundStart];
        var boundEnd = sql.IndexOf("\nmarking_need_coverage AS", boundStart, StringComparison.Ordinal);
        var boundSection = sql[boundStart..boundEnd];

        Assert.True(selectedStart > 0, "selected_marking_orders CTE must exist.");
        Assert.True(selectedStart < freeStart, "selected_marking_orders must be built before free_marking_need_coverage.");
        Assert.True(selectedStart < boundStart, "selected_marking_orders must be built before bound_marking_need_coverage.");
        Assert.Contains("INNER JOIN order_scope os ON os.id = COALESCE(mo.order_id, mo.source_order_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM markable_item_need need", freeSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN selected_marking_orders smo ON smo.order_id = need.order_id", freeSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN marking_code c ON c.marking_order_id = smo.id", freeSection, StringComparison.Ordinal);
        Assert.Contains("FROM markable_item_need need", boundSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN order_lines_scope ols ON ols.order_id = need.order_id", boundSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN marking_code c ON c.receipt_line_id = dl.id", boundSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN selected_marking_orders smo ON smo.id = c.marking_order_id", boundSection, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM marking_code c\n    INNER JOIN marking_order mo", freeSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCoverage_UsesSetBasedNeedAndOrderRollups()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var coverageStart = sql.IndexOf("free_marking_need_coverage AS", StringComparison.Ordinal);
        var rollupStart = sql.IndexOf("marking_rollup AS", StringComparison.Ordinal);
        var coverageSection = sql[coverageStart..rollupStart];
        var rollupEnd = sql.IndexOf(")\nSELECT ob.id", rollupStart, StringComparison.Ordinal);
        var rollupSection = sql[rollupStart..rollupEnd];

        Assert.Contains("free_marking_need_coverage AS", sql, StringComparison.Ordinal);
        Assert.Contains("bound_marking_need_coverage AS", sql, StringComparison.Ordinal);
        Assert.Contains("marking_need_coverage AS", sql, StringComparison.Ordinal);
        Assert.Contains("marking_code_covered_by_order AS", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT c.id) AS codes_total", coverageSection, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN marking_code_covered_by_order mcb ON mcb.order_id = ob.id", rollupSection, StringComparison.Ordinal);
        Assert.Contains("AND NOT COALESCE(mcb.has_uncovered_positive_need, FALSE) AS marking_completed", rollupSection, StringComparison.Ordinal);
        Assert.DoesNotContain("LEFT JOIN LATERAL", rollupSection, StringComparison.Ordinal);
        Assert.DoesNotContain("free_code_stats", rollupSection, StringComparison.Ordinal);
        Assert.DoesNotContain("bound_code_stats", rollupSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCoverage_PreservesExistingGtinNormalizationExpression()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("COALESCE(NULLIF(BTRIM(COALESCE(smo.gtin, c.gtin)), ''), '') = COALESCE(need.gtin, '')", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(NULLIF(BTRIM(i.gtin), ''), '') = COALESCE(need.gtin, '')", sql, StringComparison.Ordinal);
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
