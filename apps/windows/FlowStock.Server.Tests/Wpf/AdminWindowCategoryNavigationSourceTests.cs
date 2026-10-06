namespace FlowStock.Server.Tests.Wpf;

public sealed class AdminWindowCategoryNavigationSourceTests
{
    [Fact]
    public void AdminWindow_UsesPersistentCategoryNavigationAndSeparateContentPanels()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");

        Assert.Contains("x:Name=\"AdminCategoryList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"AdminCategoryList_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Система\" Tag=\"system\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Обновление\" Tag=\"update\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Веб-клиенты\" Tag=\"clients\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Печать\" Tag=\"printing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Обслуживание\" Tag=\"maintenance\"", xaml, StringComparison.Ordinal);

        foreach (var panelName in new[]
                 {
                     "SystemCategoryPanel",
                     "UpdateCategoryPanel",
                     "ClientsCategoryPanel",
                     "PrintingCategoryPanel",
                     "MaintenanceCategoryPanel"
                 })
        {
            Assert.Contains($"x:Name=\"{panelName}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Equal(5, CountOccurrences(xaml, "HorizontalScrollBarVisibility=\"Disabled\""));
        Assert.Contains("MinHeight=\"620\" MinWidth=\"820\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CategorySwitching_OnlyChangesPresentationState()
    {
        var code = ReadAppFile("AdminWindow.xaml.cs");
        var handler = ExtractMethodBody(code, "private void AdminCategoryList_SelectionChanged");

        Assert.Contains("SystemCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("UpdateCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("ClientsCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("PrintingCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("MaintenanceCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_services", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Save", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Load", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingAdminEntryPointsRemainAvailableAndNestedWindowsStayModal()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");
        var code = ReadAppFile("AdminWindow.xaml.cs");

        foreach (var handler in new[]
                 {
                     "OpenDbConnection_Click",
                     "OpenTsdDevices_Click",
                     "ChangeAdminPassword_Click",
                     "CheckUpdate_Click",
                     "InstallUpdate_Click",
                     "SaveClientBlocks_Click",
                     "RefreshPalletLabelPrinters_Click",
                     "SavePalletLabelPrinter_Click",
                     "OpenMaintenance_Click"
                 })
        {
            Assert.Contains($"Click=\"{handler}\"", xaml, StringComparison.Ordinal);
        }

        foreach (var signature in new[]
                 {
                     "private void OpenDbConnection_Click",
                     "private void OpenTsdDevices_Click",
                     "private void OpenMaintenance_Click"
                 })
        {
            var method = ExtractMethodBody(code, signature);
            Assert.Contains("Owner = this", method, StringComparison.Ordinal);
            Assert.Contains("ShowDialog()", method, StringComparison.Ordinal);
        }
    }

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");

        var nextMethod = source.IndexOf("    private ", start + signature.Length, StringComparison.Ordinal);
        var end = nextMethod >= 0 ? nextMethod : source.Length;
        return source.Substring(start, end - start);
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
