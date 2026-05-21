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
    public void TsdStorage_OrderListUsesPagedApi()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "android", "tsd", "storage.js"));

        Assert.Contains("\"limit=\" + encodeURIComponent(pageSize)", source);
        Assert.Contains("\"offset=\" + encodeURIComponent(offset)", source);
        Assert.DoesNotContain("var queryParts = [\"include_internal=1\"];", source);
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
