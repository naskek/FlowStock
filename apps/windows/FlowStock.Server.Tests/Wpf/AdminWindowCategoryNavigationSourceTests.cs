namespace FlowStock.Server.Tests.Wpf;

public sealed class AdminWindowCategoryNavigationSourceTests
{
    [Fact]
    public void AdminWindow_UsesNativeTreeNavigationAndSeparateContentPanels()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");

        Assert.Contains("Title=\"Настройки FlowStock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AdminNavigationTree\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItemChanged=\"AdminNavigationTree_SelectedItemChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Общие\" IsExpanded=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Система\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"system\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Обновление\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"update\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Доступ к блокам\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"clients\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Паллетные этикетки\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"printing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Обслуживание FlowStock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"maintenance\"", xaml, StringComparison.Ordinal);

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
    public void AdminWindow_DoesNotPaintCustomWebLikeNavigationChrome()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");

        Assert.DoesNotContain("AdminCategoryItemStyle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Cursor=\"Hand\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#F4F6F8", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#D8DDE3", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SystemColors.GrayTextBrushKey", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CategorySwitching_OnlyChangesPresentationState()
    {
        var code = ReadAppFile("AdminWindow.xaml.cs");
        var handler = ExtractMethodBody(code, "private void AdminNavigationTree_SelectedItemChanged");

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
