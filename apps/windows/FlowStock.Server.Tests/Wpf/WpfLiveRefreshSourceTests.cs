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

        Assert.Contains("LoadOrder(CaptureSelectedOrderLineId())", orderCode, StringComparison.Ordinal);
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
        Assert.Contains("LoadOrder(CaptureSelectedOrderLineId())", applyMethod, StringComparison.Ordinal);

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

    [Fact]
    public void OrderDetailsWindow_DoubleClickRequiresRealOrderLineCell()
    {
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));
        var handler = SliceMethod(code, "private void OrderLinesGrid_MouseDoubleClick", "private bool TryResolveDoubleClickedOrderLine");
        var resolver = SliceMethod(code, "private bool TryResolveDoubleClickedOrderLine", "private bool TryResolveOrderLineFromRow");
        var rowResolver = SliceMethod(code, "private bool TryResolveOrderLineFromRow", "private static T? FindVisualAncestor");

        Assert.Contains("TryResolveDoubleClickedOrderLine(e.OriginalSource as DependencyObject", handler, StringComparison.Ordinal);
        Assert.Contains("ApplySelectedOrderLine(gridItem, line);", handler, StringComparison.Ordinal);
        Assert.Contains("current is DataGridCell cell", resolver, StringComparison.Ordinal);
        Assert.Contains("FindVisualAncestor<DataGridRow>(cell)", resolver, StringComparison.Ordinal);
        Assert.Contains("current is DataGridRow directRow", resolver, StringComparison.Ordinal);
        Assert.Contains("TryGetLineFromGridContext(row.Item, out line)", rowResolver, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Controls.Primitives.ButtonBase", resolver, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Controls.Primitives.TextBoxBase", resolver, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Controls.ComboBox", resolver, StringComparison.Ordinal);
        Assert.Contains("or DatePicker", resolver, StringComparison.Ordinal);
        Assert.Contains("or DataGridColumnHeader", resolver, StringComparison.Ordinal);
        Assert.Contains("or DataGridRowHeader", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_RefreshRestoresSelectionByStableLineId()
    {
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));
        var resolve = SliceMethod(code, "private object? ResolveGridItemForLineId", "private void RestoreSelectedOrderLineById");
        var load = SliceMethod(code, "private void LoadOrder(long? reselectLineId = null)", "private void Save_Click");

        Assert.Contains("_huBinding.Lines.FirstOrDefault(row => row.Line.Id == lineId)", resolve, StringComparison.Ordinal);
        Assert.Contains("_lines.FirstOrDefault(line => line.Id == lineId)", resolve, StringComparison.Ordinal);
        Assert.Contains("var selectedLineId = reselectLineId ?? CaptureSelectedOrderLineId();", load, StringComparison.Ordinal);
        Assert.Contains("RestoreSelectedOrderLineByIdDeferred(selectedLineId);", load, StringComparison.Ordinal);
        Assert.Contains("_orderLineSelectionRestoreGeneration++", code, StringComparison.Ordinal);
        Assert.Contains("_suppressOrderLineSelectionChanged", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderLinesGrid.ItemsSource = null", code, StringComparison.Ordinal);
        Assert.Equal(1, Count(code, "OrderLinesGrid.Items.Refresh()"));
        Assert.Contains("PreserveSelectedOrderLine(() => OrderLinesGrid.Items.Refresh()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_SortingAndRefreshPreserveManualColumnWidths()
    {
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs"));
        var xaml = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml"));
        var sorting = SliceMethod(code, "private void OrderLinesGrid_Sorting", "private OrderLineView? GetSelectedOrderLine");
        var preserve = SliceMethod(code, "private void PreserveSelectedOrderLine", "private Dictionary<DataGridColumn, double> CaptureOrderLinesGridColumnWidths");
        var capture = SliceMethod(code, "private Dictionary<DataGridColumn, double> CaptureOrderLinesGridColumnWidths", "private void RestoreOrderLinesGridColumnWidths");
        var restore = SliceMethod(code, "private void RestoreOrderLinesGridColumnWidths", "private void RestoreOrderLinesGridColumnWidthsDeferred");

        Assert.Contains("OrderLinesGrid.Sorting += OrderLinesGrid_Sorting;", code, StringComparison.Ordinal);
        Assert.Contains("var columnWidths = CaptureOrderLinesGridColumnWidths();", sorting, StringComparison.Ordinal);
        Assert.Contains("RestoreOrderLinesGridColumnWidths(columnWidths);", sorting, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Handled", sorting, StringComparison.Ordinal);
        Assert.Contains("&& !_isOrderLinesGridSorting", code, StringComparison.Ordinal);

        Assert.Contains("var columnWidths = CaptureOrderLinesGridColumnWidths();", preserve, StringComparison.Ordinal);
        Assert.Contains("RestoreOrderLinesGridColumnWidthsDeferred(columnWidths);", preserve, StringComparison.Ordinal);
        Assert.Contains("column.Visibility == Visibility.Visible && column.ActualWidth > 0", capture, StringComparison.Ordinal);
        Assert.Contains("new DataGridLength(width, DataGridLengthUnitType.Pixel)", restore, StringComparison.Ordinal);

        Assert.DoesNotContain("DataGridLengthUnitType.Auto", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DataGridLengthUnitType.SizeToCells", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DataGridLengthUnitType.SizeToHeader", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderLinesGrid.ItemsSource = null", code, StringComparison.Ordinal);
        Assert.Contains("Width=\"220\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"460\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"140\"", xaml, StringComparison.Ordinal);
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

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Method end marker not found: {endMarker}");
        return source[start..end];
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
