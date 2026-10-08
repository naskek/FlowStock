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
        Assert.Contains("Tag=\"db-connection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"accounts\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Обновление\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"update\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Обновление FlowStock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Справочники\" IsExpanded=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"item-types\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"locations\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"doc-numbering\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Доступ к блокам\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"clients\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Паллетные этикетки\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"printing\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Обслуживание FlowStock\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"maintenance\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"backups\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"folders\"", xaml, StringComparison.Ordinal);

        foreach (var panelName in new[]
                 {
                     "SystemCategoryPanel",
                     "ClientsCategoryPanel",
                     "PrintingCategoryPanel",
                     "EmbeddedPagePanel"
                 })
        {
            Assert.Contains($"x:Name=\"{panelName}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Equal(3, CountOccurrences(xaml, "HorizontalScrollBarVisibility=\"Disabled\""));
        Assert.Contains("MinHeight=\"620\" MinWidth=\"900\"", xaml, StringComparison.Ordinal);
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
        Assert.DoesNotContain("UpdateCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("key == \"update\"", handler, StringComparison.Ordinal);
        Assert.Contains("ClientsCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("PrintingCategoryPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("FoldersCategoryPanel", code, StringComparison.Ordinal);
        Assert.Contains("EmbeddedPagePanel.Visibility", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_services", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Save", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Load", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsCenter_EmbedsCatalogAndBackupManagersInRightPane()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");
        var code = ReadAppFile("AdminWindow.xaml.cs");
        var host = ReadAppFile("SettingsCenterWindowPageHost.cs");
        var factory = ExtractMethodBody(code, "private HostedSettingsPage GetOrCreateEmbeddedPage");

        Assert.Contains("x:Name=\"EmbeddedPageContent\"", xaml, StringComparison.Ordinal);
        foreach (var manager in new[]
                 {
                     "ItemTypeWindow",
                     "VatRateWindow",
                     "PartnerItemSalePriceWindow",
                     "DbConnectionWindow",
                     "TsdDeviceWindow",
                     "TaraWindow",
                     "UomWindow",
                     "WriteOffReasonWindow",
                     "PackagingManagerWindow",
                     "DocNumberingSettingsWindow",
                     "BackupManagerWindow"
                 })
        {
            Assert.Contains($"new {manager}", factory, StringComparison.Ordinal);
        }

        Assert.Contains("new LocationSettingsPage", code, StringComparison.Ordinal);
        Assert.Contains("SettingsCenterWindowPageHost.Detach", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("new MaintenanceWindow", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("\"maintenance\"", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", factory, StringComparison.Ordinal);

        var detach = ExtractMethodBody(host, "public static HostedSettingsPage Detach");
        var detachContentIndex = detach.IndexOf("controller.Content = null", StringComparison.Ordinal);
        var closeControllerIndex = detach.IndexOf("controller.Close();", StringComparison.Ordinal);
        var returnPageIndex = detach.IndexOf("return new HostedSettingsPage(controller, content);", StringComparison.Ordinal);
        Assert.True(detachContentIndex >= 0, "Embedded settings content must be detached from the legacy Window.");
        Assert.True(closeControllerIndex > detachContentIndex, "Detached legacy Window must be closed after its content is removed.");
        Assert.True(returnPageIndex > closeControllerIndex, "Hosted page must be returned only after the empty legacy Window is closed.");
    }

    [Fact]
    public void MainWindow_MenuKeepsOnlySettingsAndOperationalHuRegistry()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("Header=\"Операции\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"HU Реестр...\" Click=\"OpenHuRegistry_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Настройки FlowStock...\" Click=\"OpenAdmin_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Справочники\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Центр событий...\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OpenBackupManager_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ItemRequestsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenIncomingRequests_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingInlineSettingsActionsRemainAvailable()
    {
        var xaml = ReadAppFile("AdminWindow.xaml");
        var code = ReadAppFile("AdminWindow.xaml.cs");

        foreach (var handler in new[]
                 {
                     "ChangeAdminPassword_Click",
                     "CheckUpdate_Click",
                     "InstallUpdate_Click",
                     "SaveClientBlocks_Click",
                     "RefreshPalletLabelPrinters_Click",
                     "SavePalletLabelPrinter_Click"
                 })
        {
            Assert.Contains($"Click=\"{handler}\"", xaml, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("OpenDbConnection_Click", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenTsdDevices_Click", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenMaintenance_Click", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UomWindow", "LoadUomsAsync")]
    [InlineData("WriteOffReasonWindow", "LoadReasonsAsync")]
    [InlineData("TaraWindow", "LoadTarasAsync")]
    [InlineData("ItemTypeWindow", "LoadItemTypesAsync")]
    [InlineData("VatRateWindow", "LoadVatRatesAsync")]
    [InlineData("TsdDeviceWindow", "LoadDevicesAsync")]
    [InlineData("LocationSettingsPage", "LoadLocationsAsync")]
    [InlineData("PackagingManagerWindow", "LoadItemsAsync")]
    [InlineData("PartnerItemSalePriceWindow", "LoadLookupsAsync")]
    [InlineData("BackupManagerWindow", "LoadBackupsAsync")]
    [InlineData("DocNumberingSettingsWindow", "LoadSettingsAsync")]
    public void Embedded_page_constructors_defer_IO_until_content_is_loaded(string page, string load)
    {
        var code = ReadAppFile(page + ".xaml.cs");
        var start = code.IndexOf("    public " + page + "(", StringComparison.Ordinal);
        var end = code.IndexOf("    private ", start, StringComparison.Ordinal);
        var constructor = code[start..end];
        Assert.Contains("_loading.InitializeOnLoaded(", constructor, StringComparison.Ordinal);
        Assert.Contains(load, constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGet", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Load", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter", code, StringComparison.Ordinal);
        Assert.Contains("_loading.RunAsync(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Slow_inline_settings_and_packaging_IO_are_off_dispatcher()
    {
        var admin = ReadAppFile("AdminWindow.xaml.cs");
        Assert.Contains("_clientBlocksLoading.InitializeOnLoaded(LoadClientBlocksUiAsync)", admin, StringComparison.Ordinal);
        Assert.Contains("_printersLoading.InitializeOnLoaded(LoadPalletLabelPrinterUiAsync)", admin, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(_services.WindowsPrinters.GetInstalledPrinterNames)", admin, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => _services.WpfAdminApi.TryGetClientBlocks", admin, StringComparison.Ordinal);
        var packaging = ReadAppFile("PackagingManagerWindow.xaml.cs");
        Assert.Contains("if (!_loadingItems)", packaging, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => items.SelectMany(ReadPackagingsForItem).ToArray())", packaging, StringComparison.Ordinal);
        var loading = ReadAppFile("SettingsPageLoading.cs");
        Assert.Contains("content.IsEnabled = false", loading, StringComparison.Ordinal);
        Assert.Contains("content.IsEnabled = wasEnabled", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", loading, StringComparison.Ordinal);
        var discovery = ReadAppFile(Path.Combine("Services", "PostgresDiscoveryService.cs"));
        Assert.Contains("await Task.Run(BuildCandidateEndpoints, cancellationToken).ConfigureAwait(false)", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_buttons_live_at_bottom_of_backups_and_keep_explorer_behavior()
    {
        var admin = ReadAppFile("AdminWindow.xaml");
        Assert.DoesNotContain("FoldersCategoryPanel", admin, StringComparison.Ordinal);
        Assert.DoesNotContain("Папки данных и логов", admin, StringComparison.Ordinal);
        var backup = ReadAppFile("BackupManagerWindow.xaml");
        Assert.Contains("<WrapPanel Grid.Row=\"2\"", backup, StringComparison.Ordinal);
        Assert.Contains("Content=\"Открыть папку данных\" Click=\"OpenDataFolder_Click\"", backup, StringComparison.Ordinal);
        Assert.Contains("Content=\"Открыть папку логов\" Click=\"OpenLogsFolder_Click\"", backup, StringComparison.Ordinal);
        var code = ReadAppFile("BackupManagerWindow.xaml.cs");
        Assert.Contains("OpenFolder(_services.BaseDir", code, StringComparison.Ordinal);
        Assert.Contains("OpenFolder(_services.LogsDir", code, StringComparison.Ordinal);
        Assert.Contains("Directory.Exists(path)", code, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", code, StringComparison.Ordinal);
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
