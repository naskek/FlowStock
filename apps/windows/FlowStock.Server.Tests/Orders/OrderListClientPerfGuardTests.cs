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
    public void WebOrderListMapper_UsesBatchOrderOwnedPalletSummariesForListRows()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Server", "Program.cs"));
        var mapperStart = source.IndexOf("static List<object> MapOrdersWithShipmentRemaining", StringComparison.Ordinal);
        var mapperEnd = source.IndexOf("static object MapOrderWithShipmentRemaining", mapperStart, StringComparison.Ordinal);
        var mapper = source[mapperStart..mapperEnd];

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
