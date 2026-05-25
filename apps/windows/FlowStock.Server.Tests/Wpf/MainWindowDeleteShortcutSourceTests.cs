namespace FlowStock.Server.Tests.Wpf;

public sealed class MainWindowDeleteShortcutSourceTests
{
    [Fact]
    public void MainWindow_SupportsDeleteGestureForLocationsAndPartners()
    {
        var xaml = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml"));
        var code = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"LocationsGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Extended\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"LocationsGrid_KeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PartnersGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"PartnersGrid_KeyDown\"", xaml, StringComparison.Ordinal);

        Assert.Contains("GetSelectedLocationsForDelete()", code, StringComparison.Ordinal);
        Assert.Contains("GetSelectedPartnersForDelete()", code, StringComparison.Ordinal);
        Assert.Contains("DeleteLocation_Click(LocationsGrid, new RoutedEventArgs())", code, StringComparison.Ordinal);
        Assert.Contains("DeletePartner_Click(PartnersGrid, new RoutedEventArgs())", code, StringComparison.Ordinal);
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
