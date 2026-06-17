namespace FlowStock.Server.Tests.Orders;

public sealed class OrderListClientPerfGuardTests
{
    [Fact]
    public void OperationDetailsWindow_UsesTargetedOrderCandidates_AndListMetricsForRemainingFlags()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs"));

        Assert.Contains("TryGetOperationOrderCandidates(", source);
        Assert.Contains("DocType.ProductionReceipt", source);
        Assert.Contains("DocType.Outbound", source);
        Assert.Contains("order.NeedsProductionPalletPlan", source);
        Assert.Contains("order.HasShipmentRemaining", source);
        Assert.DoesNotContain("TryGetOrders(includeInternal: true", source);
        Assert.DoesNotContain("TryGetOrdersPage(", source);
        Assert.DoesNotContain("limit: 100", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/orders?include_internal=1&limit=100", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var hasReceiptRemaining = GetOrderReceiptRemaining(order.Id)", source);
        Assert.DoesNotContain("hasShipmentRemaining = GetOrderShipmentRemaining(order.Id)", source);
    }

    [Fact]
    public void WpfReadApi_DoesNotBuildWideOperationOrderLookupPath()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs"));

        Assert.Contains("TryGetOperationOrderCandidates(", source);
        Assert.Contains("OperationOrderCandidatesApiQuery.BuildPath", source);
        Assert.DoesNotContain("/api/orders?include_internal=1&limit=100", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfCustomerOrderFlow_DoesNotCallLegacyRedistributionEndpoints()
    {
        var orderDetails = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));
        var readApi = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs"));
        var combined = orderDetails + Environment.NewLine + readApi;

        Assert.DoesNotContain("auto-redistribute-from-internal", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reserve-produced-hu", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/redistribute", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApplyCustomerOrderSaveFollowUp", combined, StringComparison.Ordinal);
        Assert.Contains("TryGetHuReservationCandidates", readApi, StringComparison.Ordinal);
        Assert.Contains("TryApplyHuReservations", readApi, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationDetailsWindow_OutboundAutofillUsesMergedHuSources_AndSuppressesDuplicateDrafts()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs"));

        Assert.Contains("GetUnshippedOutboundHuLines(", source, StringComparison.Ordinal);
        Assert.Contains("ShowOutboundAutofillMessageOnce(", source, StringComparison.Ordinal);
        Assert.Contains("TryDiscardEmptyOutboundDraftIfNeededAsync", source, StringComparison.Ordinal);
        Assert.Contains("AutoHuButton.Visibility = doc.Type == DocType.ProductionReceipt && !_hasProductionPalletPlan", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_isPartialShipment", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DocPartialCheck", source, StringComparison.Ordinal);
        Assert.Contains("AddItemButton.Visibility = _doc?.Type == DocType.Outbound ? Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("SyncCustomerOutboundFromBoundHu(_doc.Id, replaceAll: true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TsdStorage_OrderListUsesPagedApi()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "android", "tsd", "storage.js"));

        Assert.Contains("\"limit=\" + encodeURIComponent(pageSize)", source);
        Assert.Contains("\"offset=\" + encodeURIComponent(offset)", source);
        Assert.DoesNotContain("var queryParts = [\"include_internal=1\"];", source);
    }

    [Fact]
    public void WebOrderListMapper_SkipsBatchOrderOwnedPalletSummariesForLoadedListRows()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Server", "Program.cs"));
        var mapperStart = source.IndexOf("static List<object> MapOrdersWithShipmentRemaining", StringComparison.Ordinal);
        var mapperEnd = source.IndexOf("static object MapOrderWithShipmentRemaining", mapperStart, StringComparison.Ordinal);
        var mapper = source[mapperStart..mapperEnd];
        var loadedMetricsBranchStart = mapper.IndexOf("if (orderList.All(order => order.ListMetricsLoaded))", StringComparison.Ordinal);
        var batchSummaryStart = mapper.IndexOf("var palletSummariesByOrderId = BuildOrderOwnedProductionPalletSummaries(store, orderList)", StringComparison.Ordinal);
        var optimizedMetricsBranchStart = mapper.IndexOf("if (store is IOptimizedOrderListMetricsStore optimizedStore)", StringComparison.Ordinal);
        var loadedMetricsBranch = mapper[loadedMetricsBranchStart..batchSummaryStart];

        Assert.True(loadedMetricsBranchStart >= 0, "Loaded list metrics branch must be present.");
        Assert.True(batchSummaryStart > loadedMetricsBranchStart, "Batch pallet summaries must be fallback-only after loaded metrics branch.");
        Assert.True(optimizedMetricsBranchStart > batchSummaryStart, "Batch pallet summaries must still feed the optimized metrics fallback branch.");
        Assert.Contains("BuildLoadedPalletSummary(order)", loadedMetricsBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildOrderOwnedProductionPalletSummaries", loadedMetricsBranch, StringComparison.Ordinal);
        Assert.Contains("BuildOrderOwnedProductionPalletSummaries(store, orderList)", mapper, StringComparison.Ordinal);
        Assert.Contains("IOrderOwnedPalletSummaryBatchStore", source, StringComparison.Ordinal);
        Assert.Contains("GetOrderOwnedProductionPalletSummaries", source, StringComparison.Ordinal);
        Assert.Contains("MapOrderWithLoadedMetrics(Order order, ProductionPalletSummary palletSummary)", source, StringComparison.Ordinal);
        Assert.Contains("MapOrderWithMetrics(Order order, OrderListMetrics? metrics, ProductionPalletSummary palletSummary)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildOrderOwnedProductionPalletSummary(store, order.Id)", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionPalletService.BuildOrderOwnedPalletSummary(store, orderId)", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProductionPalletsByDoc(doc.Id)", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDocsByOrder(order.Id)", mapper, StringComparison.Ordinal);
    }

    [Fact]
    public void WebOrderListPerfLog_HasPhaseTimingFields_AndNoPayloadLogging()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Server", "Program.cs"));
        var logStart = source.IndexOf("static void LogOrdersListPerf", StringComparison.Ordinal);
        var logEnd = source.IndexOf("static object MapOrderWithLoadedMetrics", logStart, StringComparison.Ordinal);
        var logSection = source[logStart..logEnd];

        Assert.Contains("PERF orders-list path=/api/orders", logSection, StringComparison.Ordinal);
        Assert.Contains("include_internal={IncludeInternal}", logSection, StringComparison.Ordinal);
        Assert.Contains("include_pending_requests={IncludePendingRequests}", logSection, StringComparison.Ordinal);
        Assert.Contains("limit={Limit}", logSection, StringComparison.Ordinal);
        Assert.Contains("offset={Offset}", logSection, StringComparison.Ordinal);
        Assert.Contains("q_present={QueryPresent}", logSection, StringComparison.Ordinal);
        Assert.Contains("include_cancelled_merged={IncludeCancelledMerged}", logSection, StringComparison.Ordinal);
        Assert.Contains("rows={Rows}", logSection, StringComparison.Ordinal);
        Assert.Contains("loaded_metrics_count={LoadedMetricsCount}", logSection, StringComparison.Ordinal);
        Assert.Contains("get_orders_ms={GetOrdersMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("build_fallback_summaries_ms={BuildFallbackSummariesMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("map_ms={MapMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("total_ms={TotalMs}", logSection, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadJson", logSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReadFromJson", logSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Body", logSection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgresOrderListSqlPerfLog_HasPhaseTimingFields_AndNoSqlOrSearchPayloadLogging()
    {
        var program = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Server", "Program.cs"));
        var dataStore = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs"));
        var logStart = program.IndexOf("PERF orders-sql operation={Operation}", StringComparison.Ordinal);
        var logEnd = program.IndexOf("));", logStart, StringComparison.Ordinal);
        var logSection = program[logStart..logEnd];
        var emitStart = dataStore.IndexOf("private void EmitOrderSqlDiagnostics", StringComparison.Ordinal);
        var emitEnd = dataStore.IndexOf("private NpgsqlCommand CreateCommand", emitStart, StringComparison.Ordinal);
        var emitSection = dataStore[emitStart..emitEnd];
        var detailStart = program.IndexOf("app.MapGet(\"/api/orders/{orderId:long}\"", StringComparison.Ordinal);
        var detailEnd = program.IndexOf("app.MapGet(\"/api/orders/{orderId:long}/shipment-remaining\"", detailStart, StringComparison.Ordinal);
        var detailSection = program[detailStart..detailEnd];

        Assert.Contains("PERF orders-sql", logSection, StringComparison.Ordinal);
        Assert.Contains("operation={Operation}", logSection, StringComparison.Ordinal);
        Assert.Contains("rows={Rows}", logSection, StringComparison.Ordinal);
        Assert.Contains("q_present={QueryPresent}", logSection, StringComparison.Ordinal);
        Assert.Contains("command_index={CommandIndex}", logSection, StringComparison.Ordinal);
        Assert.Contains("command_role={CommandRole}", logSection, StringComparison.Ordinal);
        Assert.Contains("open_connection_ms={OpenConnectionMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("build_command_ms={BuildCommandMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("execute_reader_ms={ExecuteReaderMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("read_rows_ms={ReadRowsMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("total_ms={TotalMs}", logSection, StringComparison.Ordinal);
        Assert.Contains("BeginOrderListSqlDiagnostics(\"GetOrdersPage\"", program, StringComparison.Ordinal);
        Assert.Contains("BeginOrderListSqlDiagnostics(\"GetOrders\"", program, StringComparison.Ordinal);
        Assert.Contains("ExecuteOrderListReadCommand(\"GetOrdersPage\", \"orders_page_read\"", dataStore, StringComparison.Ordinal);
        Assert.Contains("ExecuteOrderListReadCommand(\"GetOrders\", \"orders_unpaged_read\"", dataStore, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginOrderListSqlDiagnostics", detailSection, StringComparison.Ordinal);
        Assert.DoesNotContain("PERF orders-sql", detailSection, StringComparison.Ordinal);
        Assert.DoesNotContain("{Sql}", logSection + emitSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CommandText", logSection + emitSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{Query}", logSection + emitSection, StringComparison.Ordinal);
        Assert.DoesNotContain("{QueryPattern}", logSection + emitSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersExplainDiagnostics_AreEnvGatedOneShotAndDoNotExposeSqlOrPlanInHttpResponse()
    {
        var program = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Server", "Program.cs"));
        var dataStore = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs"));
        var explainEndpointStart = program.IndexOf("var explainOrdersSql", StringComparison.Ordinal);
        var normalListStart = program.IndexOf("var orderService = new OrderService(store);", explainEndpointStart, StringComparison.Ordinal);
        var explainEndpointSection = program[explainEndpointStart..normalListStart];
        var explainCommandStart = dataStore.IndexOf("private static string ExecuteOrderListExplainCommand", StringComparison.Ordinal);
        var explainCommandEnd = dataStore.IndexOf("private void EmitOrderSqlDiagnostics", explainCommandStart, StringComparison.Ordinal);
        var explainCommandSection = dataStore[explainCommandStart..explainCommandEnd];
        var commandCoreStart = dataStore.IndexOf("private IReadOnlyList<Order> ExecuteOrderListReadCommandCore", StringComparison.Ordinal);
        var commandCoreEnd = dataStore.IndexOf("private static string ExecuteOrderListExplainCommand", commandCoreStart, StringComparison.Ordinal);
        var commandCoreSection = dataStore[commandCoreStart..commandCoreEnd];
        var explainBranchStart = commandCoreSection.IndexOf("if (scope?.ExplainAnalyze == true)", StringComparison.Ordinal);
        var normalReaderStart = commandCoreSection.IndexOf("var executeReaderStopwatch = Stopwatch.StartNew();", StringComparison.Ordinal);

        Assert.Contains("FLOWSTOCK_ENABLE_ORDERS_EXPLAIN", program, StringComparison.Ordinal);
        Assert.Contains("if (!ordersExplainEnabled)", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("ORDERS_EXPLAIN_DISABLED", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status403Forbidden", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref ordersExplainConsumed, 1, 0)", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("ORDERS_EXPLAIN_ALREADY_CONSUMED", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status409Conflict", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("ORDERS_EXPLAIN_REQUIRES_EMPTY_Q", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("Results.BadRequest", explainEndpointSection, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPendingCreateOrderRows", explainEndpointSection, StringComparison.Ordinal);

        Assert.Contains("var originalCommandText = command.CommandText;", explainCommandSection, StringComparison.Ordinal);
        Assert.Contains(
            "command.CommandText = \"EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS, FORMAT TEXT)\\n\" + originalCommandText;",
            explainCommandSection,
            StringComparison.Ordinal);
        Assert.True(explainBranchStart >= 0, "EXPLAIN branch must be present in the shared command helper.");
        Assert.True(normalReaderStart > explainBranchStart, "EXPLAIN branch must run before the normal SELECT reader path.");
        Assert.Contains("return Array.Empty<Order>();", commandCoreSection, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadOrder", explainCommandSection, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteNonQuery", explainEndpointSection + explainCommandSection, StringComparison.Ordinal);

        Assert.Contains("PERF ORDERS_SQL_EXPLAIN_START", program, StringComparison.Ordinal);
        Assert.Contains("PERF ORDERS_SQL_EXPLAIN_END", program, StringComparison.Ordinal);
        Assert.Contains("PlanText", program, StringComparison.Ordinal);
        Assert.Contains("ok = true", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("explain_logged = true", explainEndpointSection, StringComparison.Ordinal);
        Assert.Contains("operation = explainOperation", explainEndpointSection, StringComparison.Ordinal);
        Assert.DoesNotContain("PlanText", explainEndpointSection, StringComparison.Ordinal);
        Assert.DoesNotContain("originalCommandText", explainEndpointSection, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", explainEndpointSection, StringComparison.Ordinal);

        Assert.Contains("var orderService = new OrderService(store);", program[normalListStart..], StringComparison.Ordinal);
        Assert.Contains(".GetOrdersPage(includeInternal, normalized", program[normalListStart..], StringComparison.Ordinal);
        Assert.Contains("orders = orderService.GetOrders();", program[normalListStart..], StringComparison.Ordinal);
    }

    private static string GetRepoPath(params string[] parts)
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

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
