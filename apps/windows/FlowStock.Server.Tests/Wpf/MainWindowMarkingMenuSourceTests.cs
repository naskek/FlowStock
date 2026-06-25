namespace FlowStock.Server.Tests.Wpf;

public sealed class MainWindowMarkingMenuSourceTests
{
    [Fact]
    public void MainWindow_DoesNotExposeLegacyMarkingMenuItem()
    {
        var xaml = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml"));

        Assert.DoesNotContain("Header=\"Маркировка\" Click=\"OpenMarking_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OpenMarking_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_KeepsChestnyZnakExcelButton()
    {
        var xaml = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml"));

        Assert.Contains("x:Name=\"ExportMarkingButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Сформировать Excel ЧЗ", xaml, StringComparison.Ordinal);
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
