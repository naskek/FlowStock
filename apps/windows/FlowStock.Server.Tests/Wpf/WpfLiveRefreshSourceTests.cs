namespace FlowStock.Server.Tests.Wpf;

public sealed class WpfLiveRefreshSourceTests
{
    [Fact]
    public void MainWindow_UsesSseInvalidationWithoutPeriodicDataRefreshTimer()
    {
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml.cs"));

        Assert.Contains("_services.LiveRefresh.Register(", code, StringComparison.Ordinal);
        Assert.Contains("RefreshPendingActiveTab()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_autoRefreshTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoRefreshInterval", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailLiveRefreshes_AreReadOnlyAndDirtyGuarded()
    {
        var orderCode = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));
        var docCode = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs"));

        Assert.Contains("LoadOrder(_selectedLine?.Id)", orderCode, StringComparison.Ordinal);
        Assert.Contains("&& !_hasUnsavedChanges", orderCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_liveReadOnlyReload", orderCode, StringComparison.Ordinal);
        Assert.Contains("LoadDoc(allowAutoFill: false, includeDirectStorePresentation: false)", docCode, StringComparison.Ordinal);
        Assert.Contains("&& !_hasUnsavedChanges", docCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_LiveRefresh_UsesCanonicalHuPresentationPath()
    {
        var orderCode = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));

        var applyStart = orderCode.IndexOf("private void ApplyLiveRefresh()", StringComparison.Ordinal);
        Assert.True(applyStart >= 0);
        var applyEnd = orderCode.IndexOf("private void ApplyPendingLiveRefresh()", applyStart, StringComparison.Ordinal);
        Assert.True(applyEnd > applyStart);
        var applyMethod = orderCode[applyStart..applyEnd];
        Assert.Contains("LoadOrder(_selectedLine?.Id)", applyMethod, StringComparison.Ordinal);

        var loadStart = orderCode.IndexOf("private void LoadOrder(long? reselectLineId = null)", StringComparison.Ordinal);
        Assert.True(loadStart >= 0);
        var loadEnd = orderCode.IndexOf("private void Save_Click", loadStart, StringComparison.Ordinal);
        Assert.True(loadEnd > loadStart);
        var loadMethod = orderCode[loadStart..loadEnd];

        Assert.Contains("ApplyProductionHuCodesFromStore(_order.Id, includeFate: false);", loadMethod, StringComparison.Ordinal);
        Assert.Contains("_huBinding.EndLoad();", loadMethod, StringComparison.Ordinal);
        Assert.Contains("SyncHuBindingLines();", loadMethod, StringComparison.Ordinal);
        Assert.Contains("ScheduleDeferredHuFateDisplayLoad(_order.Id);", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("EndLoadWithoutCandidateRefresh", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservations", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservationLines", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyFinalHuBindings", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySave", loadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryUpdate", loadMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveClient_HasSingleSseEndpointAndNoDataPollingPaths()
    {
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfLiveUpdateClient.cs"));

        Assert.Contains("\"/api/live\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/orders", code, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/docs", code, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/stock", code, StringComparison.Ordinal);
    }

    private static string GetRepoFile(params string[] parts)
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

        throw new FileNotFoundException($"File not found: {string.Join(Path.DirectorySeparatorChar, parts)}");
    }
}
