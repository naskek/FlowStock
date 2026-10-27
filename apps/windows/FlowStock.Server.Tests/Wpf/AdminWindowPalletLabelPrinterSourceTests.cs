namespace FlowStock.Server.Tests.Wpf;

public sealed class AdminWindowPalletLabelPrinterSourceTests
{
    [Fact]
    public void AdminWindow_AutoSavesPrinterSelectionWithoutSaveButton()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");
        var code = ReadAppFile("AdminWindow.xaml.cs");

        Assert.Contains("Header=\"Печать паллетных этикеток\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Принтер\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PalletLabelPrinterComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEditable=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Обновить список\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RefreshPalletLabelPrinters_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"SavePalletLabelPrinterButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"SavePalletLabelPrinter_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DropDownClosed=\"PalletLabelPrinterComboBox_DropDownClosed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LostKeyboardFocus=\"PalletLabelPrinterComboBox_LostKeyboardFocus\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown=\"PalletLabelPrinterComboBox_PreviewKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PalletLabelPrinterStatusText\"", xaml, StringComparison.Ordinal);

        Assert.Contains("private async void RefreshPalletLabelPrinters_Click", code, StringComparison.Ordinal);
        Assert.Contains("private void PalletLabelPrinterComboBox_DropDownClosed", code, StringComparison.Ordinal);
        Assert.Contains("private void PalletLabelPrinterComboBox_LostKeyboardFocus", code, StringComparison.Ordinal);
        Assert.Contains("private void PalletLabelPrinterComboBox_PreviewKeyDown", code, StringComparison.Ordinal);
        Assert.Contains("private void AdminWindow_Closing", code, StringComparison.Ordinal);
        Assert.Contains("if (!TrySavePalletLabelPrinterSelection())", code, StringComparison.Ordinal);
        Assert.Contains("_updatingPalletLabelPrinterUi = true", code, StringComparison.Ordinal);
        Assert.Contains("_services.Settings.SavePalletLabelPrinterName(printerName)", code, StringComparison.Ordinal);
        Assert.Contains("PalletLabelPrinterComboBox.IsEnabled = false", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePalletLabelPrinter_Click", code, StringComparison.Ordinal);
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
