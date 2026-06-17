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
        Assert.Contains("EXISTS (\n          SELECT 1\n          FROM order_scope os\n          WHERE os.id = COALESCE(mo.order_id, mo.source_order_id)\n      )", sql, StringComparison.Ordinal);
        Assert.Contains("FROM selected_marking_orders smo", sql, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN free_code_buckets bucket ON bucket.order_id = need.order_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCodeAggregates_AreScopedToSelectedOrdersBeforeTouchingMarkingCode()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var selectedStart = sql.IndexOf("selected_marking_orders AS", StringComparison.Ordinal);
        var freeBucketsStart = sql.IndexOf("free_code_buckets AS", StringComparison.Ordinal);
        var boundBucketsStart = sql.IndexOf("bound_code_buckets AS", StringComparison.Ordinal);
        var freeBucketsEnd = sql.IndexOf("\nfree_need_matches AS", freeBucketsStart, StringComparison.Ordinal);
        var freeBucketsSection = sql[freeBucketsStart..freeBucketsEnd];
        var boundBucketsEnd = sql.IndexOf("\nbound_need_matches AS", boundBucketsStart, StringComparison.Ordinal);
        var boundBucketsSection = sql[boundBucketsStart..boundBucketsEnd];

        Assert.True(selectedStart > 0, "selected_marking_orders CTE must exist.");
        Assert.True(selectedStart < freeBucketsStart, "selected_marking_orders must be built before free code buckets.");
        Assert.True(selectedStart < boundBucketsStart, "selected_marking_orders must be built before bound code buckets.");
        Assert.Contains("EXISTS (\n          SELECT 1\n          FROM order_scope os\n          WHERE os.id = COALESCE(mo.order_id, mo.source_order_id)\n      )", sql, StringComparison.Ordinal);
        Assert.Contains("FROM selected_marking_orders smo", freeBucketsSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN marking_code c ON c.marking_order_id = smo.id", freeBucketsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT c.id", freeBucketsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("markable_item_need need", freeBucketsSection, StringComparison.Ordinal);
        Assert.Contains("FROM selected_marking_orders smo", boundBucketsSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN marking_code c ON c.marking_order_id = smo.id", boundBucketsSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN doc_lines dl ON dl.id = c.receipt_line_id", boundBucketsSection, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN order_lines_scope ols ON ols.id = dl.order_line_id\n                                    AND ols.order_id = smo.order_id", boundBucketsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT c.id", boundBucketsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("markable_item_need need", boundBucketsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM order_lines_scope ols\n    INNER JOIN items i ON i.id = ols.item_id\n    INNER JOIN doc_lines dl ON dl.order_line_id = ols.id", boundBucketsSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCoverage_UsesSetBasedNeedAndOrderRollups()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var coverageStart = sql.IndexOf("free_code_buckets AS", StringComparison.Ordinal);
        var rollupStart = sql.IndexOf("marking_rollup AS", StringComparison.Ordinal);
        var coverageSection = sql[coverageStart..rollupStart];
        var rollupEnd = sql.IndexOf(")\nSELECT ob.id", rollupStart, StringComparison.Ordinal);
        var rollupSection = sql[rollupStart..rollupEnd];

        Assert.DoesNotContain("free_code_bucket_rows AS", sql, StringComparison.Ordinal);
        Assert.Contains("free_code_buckets AS", sql, StringComparison.Ordinal);
        Assert.Contains("free_need_matches AS", sql, StringComparison.Ordinal);
        Assert.Contains("free_marking_need_coverage AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("bound_code_bucket_rows AS", sql, StringComparison.Ordinal);
        Assert.Contains("bound_code_buckets AS", sql, StringComparison.Ordinal);
        Assert.Contains("bound_need_matches AS", sql, StringComparison.Ordinal);
        Assert.Contains("bound_marking_need_coverage AS", sql, StringComparison.Ordinal);
        Assert.Contains("marking_need_coverage AS", sql, StringComparison.Ordinal);
        Assert.Contains("marking_code_covered_by_order AS", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS free_qty", coverageSection, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS bound_qty", coverageSection, StringComparison.Ordinal);
        Assert.Contains("SUM(matched_qty) AS codes_total", coverageSection, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", coverageSection, StringComparison.Ordinal);
        Assert.Contains("AND bucket.item_id <> need.item_id", coverageSection, StringComparison.Ordinal);
        Assert.Contains("AND NULLIF(BTRIM(need.gtin), '') IS NOT NULL", coverageSection, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN marking_code_covered_by_order mcb ON mcb.order_id = ob.id", rollupSection, StringComparison.Ordinal);
        Assert.Contains("AND NOT COALESCE(mcb.has_uncovered_positive_need, FALSE) AS marking_completed", rollupSection, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT c.id", coverageSection, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT(DISTINCT c.id)", coverageSection, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM markable_item_need need\n    INNER JOIN selected_marking_orders", coverageSection, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM markable_item_need need\n    INNER JOIN order_lines_scope", coverageSection, StringComparison.Ordinal);
        Assert.DoesNotContain("LEFT JOIN LATERAL", rollupSection, StringComparison.Ordinal);
        Assert.DoesNotContain("free_code_stats", rollupSection, StringComparison.Ordinal);
        Assert.DoesNotContain("bound_code_stats", rollupSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCoverage_SelectedMarkingOrdersAreUniqueBeforeCountingCodes()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var selectedStart = sql.IndexOf("selected_marking_orders AS", StringComparison.Ordinal);
        var freeBucketsStart = sql.IndexOf("\nfree_code_buckets AS", selectedStart, StringComparison.Ordinal);
        var selectedSection = sql[selectedStart..freeBucketsStart];

        Assert.Contains("SELECT mo.id,", selectedSection, StringComparison.Ordinal);
        Assert.Contains("FROM marking_order mo", selectedSection, StringComparison.Ordinal);
        Assert.Contains("EXISTS (\n          SELECT 1\n          FROM order_scope os\n          WHERE os.id = COALESCE(mo.order_id, mo.source_order_id)\n      )", selectedSection, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN needed_marking_keys", selectedSection, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN markable_item_need", selectedSection, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN marking_code", selectedSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCoverage_MatchesNeedsOnlyAfterBucketAggregation()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var freeMatchesStart = sql.IndexOf("free_need_matches AS", StringComparison.Ordinal);
        var freeCoverageStart = sql.IndexOf("free_marking_need_coverage AS", StringComparison.Ordinal);
        var freeMatchesSection = sql[freeMatchesStart..freeCoverageStart];
        var boundMatchesStart = sql.IndexOf("bound_need_matches AS", StringComparison.Ordinal);
        var boundCoverageStart = sql.IndexOf("bound_marking_need_coverage AS", StringComparison.Ordinal);
        var boundMatchesSection = sql[boundMatchesStart..boundCoverageStart];

        Assert.Contains("INNER JOIN free_code_buckets bucket ON bucket.order_id = need.order_id", freeMatchesSection, StringComparison.Ordinal);
        Assert.Contains("AND bucket.item_id = need.item_id", freeMatchesSection, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", freeMatchesSection, StringComparison.Ordinal);
        Assert.Contains("AND bucket.item_id <> need.item_id", freeMatchesSection, StringComparison.Ordinal);
        Assert.Contains("bucket.normalized_gtin = COALESCE(need.gtin, '')", freeMatchesSection, StringComparison.Ordinal);
        Assert.DoesNotContain("marking_code c", freeMatchesSection, StringComparison.Ordinal);

        Assert.Contains("INNER JOIN bound_code_buckets bucket ON bucket.order_id = need.order_id", boundMatchesSection, StringComparison.Ordinal);
        Assert.Contains("AND bucket.item_id = need.item_id", boundMatchesSection, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", boundMatchesSection, StringComparison.Ordinal);
        Assert.Contains("AND bucket.item_id <> need.item_id", boundMatchesSection, StringComparison.Ordinal);
        Assert.Contains("bucket.normalized_gtin = COALESCE(need.gtin, '')", boundMatchesSection, StringComparison.Ordinal);
        Assert.DoesNotContain("marking_code c", boundMatchesSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderListMarkingCoverage_PreservesExistingGtinNormalizationExpression()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("NULLIF(BTRIM(i.gtin), '') AS gtin", sql, StringComparison.Ordinal);
        Assert.Contains("AND NULLIF(BTRIM(i.gtin), '') IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(NULLIF(BTRIM(COALESCE(smo.gtin, c.gtin)), ''), '') AS normalized_gtin", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(NULLIF(BTRIM(i.gtin), ''), '') AS normalized_gtin", sql, StringComparison.Ordinal);
        Assert.Contains("AND NULLIF(BTRIM(need.gtin), '') IS NOT NULL", sql, StringComparison.Ordinal);
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
