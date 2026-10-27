using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FlowStock.DesktopUpdate;
using FlowStock.Core.Models;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfPanel = System.Windows.Controls.Panel;
using WpfTreeViewItem = System.Windows.Controls.TreeViewItem;

namespace FlowStock.App;

public partial class AdminWindow : Window
{
    private readonly AppServices? _productionServices;
    public bool IsUiPreview => _productionServices is null;
    private AppServices _services => _productionServices
        ?? throw new InvalidOperationException(UiPreviewContext.OperationUnavailable);
    private readonly SettingsPageLoading _clientBlocksLoading = null!;
    private readonly SettingsPageLoading _printersLoading = null!;
    private readonly Action? _catalogsChanged;
    private readonly Dictionary<string, WpfCheckBox> _clientBlockBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostedSettingsPage> _embeddedPages = new(StringComparer.OrdinalIgnoreCase);
    private string? _palletLabelPrinterEnvironmentOverride;
    private IReadOnlyList<string> _installedPalletLabelPrinterNames = Array.Empty<string>();
    private bool _palletLabelPrinterUiLoaded;
    private bool _updatingPalletLabelPrinterUi;
    private CancellationTokenSource? _updateCheckCancellation;
    private int _updateCheckGeneration;
    private DesktopUpdateCheckResult? _updateCheckResult;

    public AdminWindow(AppServices services, Action? catalogsChanged = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _productionServices = services;
        _catalogsChanged = catalogsChanged;

        InitializeComponent();
        SystemSettingsNavigationItem.IsSelected = true;
        _clientBlocksLoading = new SettingsPageLoading(ClientsCategoryPanel, _services.AppLogger);
        _printersLoading = new SettingsPageLoading(PrintingCategoryPanel, _services.AppLogger);
        _clientBlocksLoading.InitializeOnLoaded(LoadClientBlocksUiAsync);
        _printersLoading.InitializeOnLoaded(LoadPalletLabelPrinterUiAsync);
        Closing += AdminWindow_Closing;
        InstalledBuildText.Text = $"Установлено: {AppRuntimeInfo.Current.ProductVersion}\n{AppRuntimeInfo.Current.SourceCommit}"
            + (AppRuntimeInfo.IsSourceRun ? "\nРежим: запуск из исходного checkout" : string.Empty);
        Loaded += async (_, _) => await CheckForUpdateAsync();
        Closed += (_, _) =>
        {
            CancelUpdateCheck();
            _catalogsChanged?.Invoke();
        };
    }

    private void AdminNavigationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (AdminNavigationTree.SelectedItem is not WpfTreeViewItem selectedItem)
        {
            return;
        }

        var key = selectedItem.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        SystemCategoryPanel.Visibility = key == "system" ? Visibility.Visible : Visibility.Collapsed;
        ClientsCategoryPanel.Visibility = key == "clients" ? Visibility.Visible : Visibility.Collapsed;
        PrintingCategoryPanel.Visibility = key == "printing" ? Visibility.Visible : Visibility.Collapsed;

        if (!EmbeddedPageTitles.TryGetValue(key, out var title))
        {
            EmbeddedPagePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var page = GetOrCreateEmbeddedPage(key);
        EmbeddedPageTitleText.Text = title;
        EmbeddedPageContent.Content = page.Content;
        EmbeddedPagePanel.Visibility = Visibility.Visible;
    }

    private static IReadOnlyDictionary<string, string> EmbeddedPageTitles { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["db-connection"] = "Подключение к БД",
            ["accounts"] = "Учётные записи",
            ["item-types"] = "Типы номенклатуры",
            ["vat-rates"] = "Ставки НДС",
            ["partner-prices"] = "Цены клиентов",
            ["locations"] = "Места хранения",
            ["tara"] = "Тара",
            ["uom"] = "Единицы измерения",
            ["write-off-reasons"] = "Причины списания",
            ["packaging"] = "Упаковочные единицы / кратности",
            ["doc-numbering"] = "Нумерация документов",
            ["backups"] = "Резервные копии"
        };

    private HostedSettingsPage GetOrCreateEmbeddedPage(string key)
    {
        if (_embeddedPages.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = IsUiPreview ? CreateUiPreviewPage(key) : key switch
        {
            "db-connection" => SettingsCenterWindowPageHost.Detach(new DbConnectionWindow(_services)),
            "accounts" => SettingsCenterWindowPageHost.Detach(new TsdDeviceWindow(_services)),
            "item-types" => SettingsCenterWindowPageHost.Detach(new ItemTypeWindow(_services, _catalogsChanged)),
            "vat-rates" => SettingsCenterWindowPageHost.Detach(new VatRateWindow(_services)),
            "partner-prices" => SettingsCenterWindowPageHost.Detach(new PartnerItemSalePriceWindow(_services)),
            "locations" => CreateLocationSettingsPage(),
            "tara" => SettingsCenterWindowPageHost.Detach(new TaraWindow(_services, _catalogsChanged)),
            "uom" => SettingsCenterWindowPageHost.Detach(new UomWindow(_services, _catalogsChanged)),
            "write-off-reasons" => SettingsCenterWindowPageHost.Detach(new WriteOffReasonWindow(_services, _catalogsChanged)),
            "packaging" => SettingsCenterWindowPageHost.Detach(new PackagingManagerWindow(_services)),
            "doc-numbering" => SettingsCenterWindowPageHost.Detach(new DocNumberingSettingsWindow(_services)),
            "backups" => SettingsCenterWindowPageHost.Detach(new BackupManagerWindow(_services)),
            _ => throw new InvalidOperationException($"Неизвестная страница настроек: {key}")
        };
        _embeddedPages[key] = created;
        return created;
    }

    private HostedSettingsPage CreateLocationSettingsPage()
    {
        var page = new LocationSettingsPage(_services, _catalogsChanged);
        return new HostedSettingsPage(page, page);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (IsUiPreview) return;
        if (_updateCheckCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _updateCheckCancellation = cancellation;
        var generation = ++_updateCheckGeneration;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Проверка production server...";
        UpdateTargetText.Text = string.Empty;
        UpdateDiagnosticsText.Text = string.Empty;
        try
        {
            if (AppRuntimeInfo.IsDevelopmentCheckout)
            {
                throw new InvalidOperationException("Production self-update из D:\\FlowStock-dev запрещён.");
            }

            var updateServerBaseUri = DesktopUpdateEndpointResolver.Resolve();
            UpdateStatusText.Text = $"Проверка доверенного сервера обновлений {updateServerBaseUri.GetLeftPart(UriPartial.Authority)}...";
            using var http = DesktopUpdateHttpClientFactory.Create(TimeSpan.FromSeconds(30));
            var runner = new ProcessRunner();
            var checker = new DesktopUpdateChecker(http, new GitRepositoryClient(runner));
            var result = await checker.CheckAsync(
                updateServerBaseUri,
                DesktopUpdateConstants.DefaultRepositoryRoot,
                AppRuntimeInfo.Current,
                cancellation.Token);
            if (generation != _updateCheckGeneration || cancellation.IsCancellationRequested || !IsLoaded)
            {
                return;
            }

            _updateCheckResult = result;
            UpdateStatusText.Text = result.Message;
            UpdateTargetText.Text = result.Target is null
                ? string.Empty
                : $"Production target: {result.Target.ProductVersion}\n{result.Target.SourceCommit}";
            UpdateDiagnosticsText.Text = string.IsNullOrWhiteSpace(result.Diagnostics)
                ? string.Empty
                : $"Состояние repository root (только диагностика):\n{result.Diagnostics}";
            InstallUpdateButton.IsEnabled = result.CanUpdate;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == _updateCheckGeneration && IsLoaded)
            {
                UpdateStatusText.Text = $"Невозможно проверить обновление: {exception.Message}";
            }
        }
        finally
        {
            if (generation == _updateCheckGeneration)
            {
                _updateCheckCancellation = null;
                CheckUpdateButton.IsEnabled = IsLoaded;
            }

            cancellation.Dispose();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        var result = _updateCheckResult;
        if (result?.CanUpdate != true || result.Target is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"Обновить FlowStock и перезапустить приложение?\n\n"
            + $"Сейчас: {result.Installed.ProductVersion}\n{result.Installed.SourceCommit}\n\n"
            + $"Target: {result.Target.ProductVersion}\n{result.Target.SourceCommit}\n\n"
            + "Завершите незаконченные редактирования в открытых формах.",
            "Обновление FlowStock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var paths = new DesktopUpdatePaths();
            var runner = new ProcessRunner();
            var bootstrap = new UpdateBootstrapService(
                paths,
                new RuntimeStateManager(paths),
                new GitRepositoryClient(runner),
                runner);
            var session = await bootstrap.PrepareAndLaunchAsync(
                result.UpdateServerBaseUri,
                DesktopUpdateConstants.DefaultRepositoryRoot,
                result.Installed,
                result.Target,
                AppRuntimeInfo.IsSourceRun,
                CancellationToken.None);
            UpdateStatusText.Text = "Updater подготовлен; FlowStock завершает работу...";
            await bootstrap.WaitUntilReadyAsync(session, TimeSpan.FromSeconds(30), CancellationToken.None);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            _services.AdminLogger.Error("desktop_update bootstrap failed", exception);
            MessageBox.Show(exception.Message, "Обновление FlowStock", MessageBoxButton.OK, MessageBoxImage.Error);
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private void CancelUpdateCheck()
    {
        _updateCheckGeneration++;
        _updateCheckCancellation?.Cancel();
        _updateCheckCancellation = null;
    }

    private void ChangeAdminPassword_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        if (_services.AdminAuth.EnsureAdminPasswordExists())
        {
            var prompt = new PasswordPromptWindow(_services.AdminAuth) { Owner = this };
            if (prompt.ShowDialog() != true)
            {
                _services.AdminLogger.Info("admin_change_password aborted: current password prompt cancelled");
                return;
            }
        }

        var setPassword = new SetAdminPasswordWindow(_services.AdminAuth) { Owner = this };
        if (setPassword.ShowDialog() != true)
        {
            _services.AdminLogger.Info("admin_change_password aborted: new password dialog cancelled");
            return;
        }

        _services.AdminLogger.Info("admin_password changed from ui");
        MessageBox.Show("Пароль администратора изменён.", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SaveClientBlocks_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        try
        {
            var settings = _clientBlockBoxes
                .Select(entry => new ClientBlockSetting(entry.Key, entry.Value.IsChecked == true))
                .ToList();
            var saved = await _services.WpfAdminApi
                .TrySaveClientBlocksAsync(settings)
                .ConfigureAwait(true);
            if (!saved)
            {
                throw new InvalidOperationException("Не удалось сохранить доступ к веб-блокам через сервер.");
            }

            ClientBlocksStatusText.Text = "Доступ к веб-блокам сохранен. Изменения применятся после обновления страницы у пользователей.";
            _services.AdminLogger.Info("admin_client_blocks saved");
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_client_blocks save failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task LoadClientBlocksUiAsync() => _clientBlocksLoading.RunAsync(async () =>
    {
        try
        {
            var settings = await Task.Run(() => _services.WpfAdminApi.TryGetClientBlocks(out var apiSettings)
                ? apiSettings
                : Array.Empty<ClientBlockSetting>());
            var states = ClientBlockCatalog.MergeWithDefaults(settings);
            _clientBlockBoxes.Clear();
            PopulateClientBlockPanel(
                PcBlocksPanel,
                ClientBlockCatalog.All.Where(definition => definition.Client == "PC"),
                states);
            PopulateClientBlockPanel(
                TsdMainBlocksPanel,
                ClientBlockCatalog.All.Where(definition => definition.Client == "TSD" && definition.Section == "Основные"),
                states);
            PopulateClientBlockPanel(
                TsdOperationBlocksPanel,
                ClientBlockCatalog.All.Where(definition => definition.Client == "TSD" && definition.Section == "Операции"),
                states);
            ClientBlocksStatusText.Text = "Отключенные блоки скрываются у всех пользователей веб-клиентов.";
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_client_blocks load failed", ex);
            ClientBlocksStatusText.Text = "Не удалось загрузить доступ к веб-блокам.";
            SaveClientBlocksButton.IsEnabled = false;
        }
    });

    private Task LoadPalletLabelPrinterUiAsync() => _printersLoading.RunAsync(async () =>
    {
        var settings = await Task.Run(_services.Settings.Load);
        var savedPrinterName = PalletLabelPrinterNameResolver.NormalizePrinterName(
            settings.PalletLabels?.PrinterName);
        _palletLabelPrinterEnvironmentOverride = PalletLabelPrinterNameResolver.ResolveEnvironmentOverride(
            Environment.GetEnvironmentVariable(PalletLabelPrinterNameResolver.EnvironmentVariableName));

        var localSettingEnabled = _palletLabelPrinterEnvironmentOverride == null;
        PalletLabelPrinterComboBox.IsEnabled = localSettingEnabled;
        await RefreshPalletLabelPrintersCoreAsync(savedPrinterName, statusPrefix: null);
        _palletLabelPrinterUiLoaded = true;
    });

    private async void RefreshPalletLabelPrinters_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        await RefreshPalletLabelPrintersAsync(PalletLabelPrinterComboBox.Text, statusPrefix: null);
    }

    private void PalletLabelPrinterComboBox_DropDownClosed(object sender, EventArgs e)
    {
        TrySavePalletLabelPrinterSelection();
    }

    private void PalletLabelPrinterComboBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!PalletLabelPrinterComboBox.IsDropDownOpen)
        {
            TrySavePalletLabelPrinterSelection();
        }
    }

    private void PalletLabelPrinterComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !PalletLabelPrinterComboBox.IsDropDownOpen)
        {
            TrySavePalletLabelPrinterSelection();
        }
    }

    private void AdminWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save manually typed text even when the ComboBox still has focus.
        if (!TrySavePalletLabelPrinterSelection())
        {
            e.Cancel = true;
        }
    }

    private bool TrySavePalletLabelPrinterSelection()
    {
        if (IsUiPreview || !_palletLabelPrinterUiLoaded || _updatingPalletLabelPrinterUi)
        {
            return true;
        }

        _palletLabelPrinterEnvironmentOverride = PalletLabelPrinterNameResolver.ResolveEnvironmentOverride(
            Environment.GetEnvironmentVariable(PalletLabelPrinterNameResolver.EnvironmentVariableName));
        if (_palletLabelPrinterEnvironmentOverride != null)
        {
            PalletLabelPrinterComboBox.IsEnabled = false;
            PalletLabelPrinterStatusText.Text = BuildPalletLabelPrinterStatus(
                PalletLabelPrinterSelectionState.Build(
                    _installedPalletLabelPrinterNames, PalletLabelPrinterComboBox.Text),
                enumerationError: null, statusPrefix: null);
            return true;
        }

        var printerName = PalletLabelPrinterNameResolver.NormalizePrinterName(PalletLabelPrinterComboBox.Text);
        try
        {
            if (_services.Settings.SavePalletLabelPrinterName(printerName))
            {
                _services.AdminLogger.Info("admin_pallet_label_printer saved");
            }

            PalletLabelPrinterStatusText.Text = BuildPalletLabelPrinterStatus(
                PalletLabelPrinterSelectionState.Build(_installedPalletLabelPrinterNames, printerName),
                enumerationError: null,
                statusPrefix: "Настройка сохранена и будет использована при следующей печати.");
            return true;
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_pallet_label_printer save failed", ex);
            PalletLabelPrinterStatusText.Text =
                $"Не удалось сохранить настройку принтера. Выбранное значение не применено; " +
                $"продолжает действовать прежняя настройка. Повторите ввод или выбор. {ex.Message}";
            return false;
        }
    }

    private Task RefreshPalletLabelPrintersAsync(string? currentPrinterName, string? statusPrefix) =>
        _printersLoading.RunAsync(() => RefreshPalletLabelPrintersCoreAsync(currentPrinterName, statusPrefix));

    private async Task RefreshPalletLabelPrintersCoreAsync(string? currentPrinterName, string? statusPrefix)
    {
        PalletLabelPrinterSelectionState state;
        string? enumerationError = null;
        try
        {
            state = PalletLabelPrinterSelectionState.Build(
                await Task.Run(_services.WindowsPrinters.GetInstalledPrinterNames),
                currentPrinterName);
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_pallet_label_printers enumerate failed", ex);
            enumerationError = ex.Message;
            state = PalletLabelPrinterSelectionState.Build(
                Array.Empty<string>(),
                currentPrinterName);
        }

        _installedPalletLabelPrinterNames = state.InstalledPrinterNames;
        _updatingPalletLabelPrinterUi = true;
        try
        {
            PalletLabelPrinterComboBox.ItemsSource = new[] { string.Empty }
                .Concat(state.InstalledPrinterNames)
                .ToArray();
            PalletLabelPrinterComboBox.Text = state.PrinterName;
        }
        finally
        {
            _updatingPalletLabelPrinterUi = false;
        }

        PalletLabelPrinterStatusText.Text = BuildPalletLabelPrinterStatus(
            state,
            enumerationError,
            statusPrefix);
    }

    private string BuildPalletLabelPrinterStatus(
        PalletLabelPrinterSelectionState state,
        string? enumerationError,
        string? statusPrefix)
    {
        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(statusPrefix))
        {
            messages.Add(statusPrefix);
        }

        if (_palletLabelPrinterEnvironmentOverride != null)
        {
            messages.Add(
                $"Активный принтер задан переменной {PalletLabelPrinterNameResolver.EnvironmentVariableName}: " +
                $"«{_palletLabelPrinterEnvironmentOverride}». Локальный выбор отключён.");
        }

        if (!string.IsNullOrWhiteSpace(enumerationError))
        {
            var manualInputHint = _palletLabelPrinterEnvironmentOverride == null
                ? " Можно ввести точное имя вручную."
                : string.Empty;
            messages.Add(
                $"Не удалось получить список принтеров Windows.{manualInputHint} {enumerationError}");
        }
        else if (state.InstalledPrinterNames.Count == 0)
        {
            messages.Add(_palletLabelPrinterEnvironmentOverride == null
                ? "Установленные принтеры не найдены. Можно ввести точное имя вручную."
                : "Установленные принтеры не найдены.");
        }

        if (state.IsPrinterMissing)
        {
            messages.Add(
                $"Принтер «{state.PrinterName}» сейчас не найден в Windows. " +
                "Имя в поле оставлено без изменений.");
        }

        if (messages.Count == 0)
        {
            messages.Add("Выберите установленный принтер или оставьте поле пустым.");
        }

        return string.Join(" ", messages);
    }

    private void PopulateClientBlockPanel(
        WpfPanel panel,
        IEnumerable<ClientBlockDefinition> definitions,
        IReadOnlyDictionary<string, bool> states)
    {
        panel.Children.Clear();
        foreach (var definition in definitions)
        {
            var isEnabled = states.TryGetValue(definition.Key, out var value) ? value : true;
            var checkBox = new WpfCheckBox
            {
                Content = definition.Label,
                IsChecked = isEnabled,
                Margin = new Thickness(0, 0, 0, 6)
            };
            panel.Children.Add(checkBox);
            _clientBlockBoxes[definition.Key] = checkBox;
        }
    }
}
