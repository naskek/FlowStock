namespace FlowStock.Server.Tests.Wpf;

public sealed class AdminWindowPalletLabelPrinterSourceTests
{
    [Fact]
    public void AdminWindow_ContainsEditablePalletLabelPrinterSettingsAndButtonHandlers()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");
        var code = ReadAppFile("AdminWindow.xaml.cs");

        Assert.Contains("Header=\"Печать паллетных этикеток\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Принтер\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PalletLabelPrinterComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEditable=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Обновить список\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RefreshPalletLabelPrinters_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Сохранить\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SavePalletLabelPrinter_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PalletLabelPrinterStatusText\"", xaml, StringComparison.Ordinal);

        Assert.Contains("private void RefreshPalletLabelPrinters_Click", code, StringComparison.Ordinal);
        Assert.Contains("private void SavePalletLabelPrinter_Click", code, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string fileName)
    {
        return File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", fileName));
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
