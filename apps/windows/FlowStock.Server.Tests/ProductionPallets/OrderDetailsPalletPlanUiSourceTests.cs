namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class OrderDetailsPalletPlanUiSourceTests
{
    [Fact]
    public void PalletButtons_AllowOnlyInternalShippedCleanup_AndKeepPlanningBlocked()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains(
            "var canPlan = _orderId.HasValue && _order?.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "&& _order?.Status is not (OrderStatus.Cancelled or OrderStatus.Merged)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "&& (_order?.Status != OrderStatus.Shipped || _order.Type == OrderType.Internal)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("&& HasOpenProductionPalletPlan(_orderId.Value);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintedPalletCleanup_KeepsExistingMarkingWarning()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("if (dialog.SelectedRowsHaveMarkingWarning)", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
