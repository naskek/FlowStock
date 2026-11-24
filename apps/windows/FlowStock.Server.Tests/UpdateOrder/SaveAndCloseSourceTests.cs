namespace FlowStock.Server.Tests.UpdateOrder;

public sealed class SaveAndCloseSourceTests
{
    [Fact]
    public void CloseSave_AwaitsServerWithoutReload_AndGuardsConcurrentActions()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "apps/windows/FlowStock.App/OrderDetailsWindow.xaml.cs"));
        var save = source[source.IndexOf("private async void Save_Click", StringComparison.Ordinal)..source.IndexOf("private async void ExportMarking_Click", StringComparison.Ordinal)];
        Assert.Contains("await _services.WpfUpdateOrders.UpdateOrderAsync", save);
        Assert.Contains("reloadAfterPersist: false", save);
        Assert.DoesNotContain("GetAwaiter()", save);
        Assert.DoesNotContain("ReloadCanonicalOrderStateAfterPersist", save);
        Assert.Contains("_saveInProgress ||", save);
        Assert.Contains("content.IsEnabled = false", save);
        Assert.Contains("finally", save);
        Assert.Contains("content.IsEnabled = true", save);
        Assert.Contains("if (_saveInProgress && !_allowCloseWithoutPrompt)", source);
        Assert.Contains("&& !_saveInProgress", source);
        Assert.Contains("if (reloadAfterPersist)", source);
    }
}
