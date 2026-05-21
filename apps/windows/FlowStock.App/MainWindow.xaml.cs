using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExcelDataReader;
using FlowStock.App.Services;
using FlowStock.Core.Models;
using Microsoft.Win32;
using Npgsql;

namespace FlowStock.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Item> _items = new();
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Uom> _uoms = new();
    private readonly ObservableCollection<Tara> _taras = new();
    private readonly ObservableCollection<PartnerRow> _partners = new();
    private readonly ObservableCollection<Doc> _docs = new();
    private readonly ObservableCollection<Order> _orders = new();
    private readonly ObservableCollection<WarehouseBundleListRow> _warehouseBundles = new();
    private readonly ObservableCollection<StockDisplayRow> _stock = new();
    private readonly ObservableCollection<WarehouseProductionStateDisplayRow> _warehouseProductionStateRows = new();
    private readonly ObservableCollection<LowStockDisplayRow> _lowStock = new();
    private readonly ObservableCollection<ProductionNeedDisplayRow> _productionNeedRows = new();
    private readonly ObservableCollection<StockLocationFilterOption> _stockLocationFilters = new();
    private readonly ObservableCollection<StockHuFilterOption> _stockHuFilters = new();
    private readonly ObservableCollection<StockItemTypeFilterOption> _stockItemTypeFilters = new();
    private readonly ObservableCollection<KmCodeBatch> _kmBatches = new();
    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly HashSet<long> _expandedStockItemIds = new();
    private bool _autoRefreshInProgress;
    private bool _serverApiUnavailableAtStartup;
    private bool _suppressStockFilterSelectionChanged;
    private static bool _excelEncodingRegistered;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StockRefreshDebounceInterval = TimeSpan.FromMilliseconds(200);
    private DispatcherTimer? _stockRefreshDebounceTimer;
    private bool _stockRefreshDebounceTickAttached;
    private string? _pendingStockSearch;
    private string? _warehouseProductionStateFingerprint;
    private readonly List<DocTypeFilterOption> _docTypeFilters = new()
    {
        new DocTypeFilterOption(null, "Все"),
        new DocTypeFilterOption(DocType.Inbound, "Приемка"),
        new DocTypeFilterOption(DocType.ProductionReceipt, "Выпуск продукции"),
        new DocTypeFilterOption(DocType.Outbound, "Отгрузка"),
        new DocTypeFilterOption(DocType.Move, "Перемещение"),
        new DocTypeFilterOption(DocType.WriteOff, "Списание"),
        new DocTypeFilterOption(DocType.Inventory, "Инвентаризация")
    };
    private readonly List<DocStatusFilterOption> _docStatusFilters = new()
    {
        new DocStatusFilterOption(null, "Все"),
        new DocStatusFilterOption(DocStatus.Draft, "Черновик"),
        new DocStatusFilterOption(DocStatus.Closed, "Проведена")
    };
    private Item? _selectedItem;
    private Location? _selectedLocation;
    private Partner? _selectedPartner;
    private bool _adminDeleteModeEnabled = false;
    private const int TabStatusIndex = 0;
    private const int TabProductionNeedIndex = 1;
    private const int TabDocsIndex = 2;
    private const int TabOrdersIndex = 3;
    private const int TabTasksIndex = 4;
    private const int TabItemsIndex = 5;
    private const int TabLocationsIndex = 6;
    private const int TabPartnersIndex = 7;
    private const int TabKmIndex = 8;
    private const int OrdersPageSize = 15;
    private int _ordersPagedDepth;
    private bool _ordersHasMore;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        LocationsGrid.ItemsSource = _locations;
        PartnersGrid.ItemsSource = _partners;
        DocsGrid.ItemsSource = _docs;
        OrdersGrid.ItemsSource = _orders;
        WarehouseBundlesGrid.ItemsSource = _warehouseBundles;
        WarehouseBundleFilterCombo.ItemsSource = new[]
        {
            new WarehouseBundleFilterOption(null, "Все"),
            new WarehouseBundleFilterOption("SUBMITTED", "На подтверждении"),
            new WarehouseBundleFilterOption("IN_EXECUTION", "В работе"),
            new WarehouseBundleFilterOption("EXECUTED", "Исполнено ТСД"),
            new WarehouseBundleFilterOption("COMPLETED", "Проведено"),
            new WarehouseBundleFilterOption("REJECTED", "Отклонено")
        };
        WarehouseBundleFilterCombo.DisplayMemberPath = nameof(WarehouseBundleFilterOption.Label);
        WarehouseBundleFilterCombo.SelectedIndex = 1;
        StockGrid.ItemsSource = _stock;
        WarehouseProductionStateGrid.ItemsSource = _warehouseProductionStateRows;
        LowStockGrid.ItemsSource = _lowStock;
        ProductionNeedGrid.ItemsSource = _productionNeedRows;
        StockLocationFilter.ItemsSource = _stockLocationFilters;
        StockHuFilter.ItemsSource = _stockHuFilters;
        StockItemTypeFilter.ItemsSource = _stockItemTypeFilters;
        KmBatchesGrid.ItemsSource = _kmBatches;
        DocsTypeFilter.ItemsSource = _docTypeFilters;
        DocsTypeFilter.SelectedIndex = 0;
        DocsStatusFilter.ItemsSource = _docStatusFilters;
        DocsStatusFilter.SelectedIndex = 0;
        ApplyDeleteMode();
        ApplyExperimentalTabVisibility();
        UpdateStockModeUi();

        TryLoadAllOnStartup();
        ClearItemForm();
        ClearLocationForm();
        ClearPartnerForm();

        _autoRefreshTimer = new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void ApplyDeleteMode()
    {
        Title = _adminDeleteModeEnabled
            ? "FlowStock [режим удаления: админ]"
            : "FlowStock";
        UpdateDeleteButtonsAvailability();
    }

    private void ApplyExperimentalTabVisibility()
    {
        if (WarehouseTasksTab == null)
        {
            return;
        }

        WarehouseTasksTab.Visibility = ExperimentalFeatureFlags.WarehouseTasksEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateDeleteButtonsAvailability()
    {
        if (ItemDeleteButton != null)
        {
            var hasSelection = _selectedItem != null
                               || (ItemsGrid?.SelectedItems?.Count ?? 0) > 0;
            ItemDeleteButton.IsEnabled = hasSelection;
        }

        if (ItemEditButton != null)
        {
            ItemEditButton.IsEnabled = _selectedItem != null;
        }

        if (ItemPackagingButton != null)
        {
            ItemPackagingButton.IsEnabled = _selectedItem != null;
        }

        if (LocationDeleteButton != null)
        {
            LocationDeleteButton.IsEnabled = _selectedLocation != null;
        }

        if (LocationEditButton != null)
        {
            LocationEditButton.IsEnabled = _selectedLocation != null;
        }

        if (PartnerDeleteButton != null)
        {
            PartnerDeleteButton.IsEnabled = _selectedPartner != null;
        }

        if (PartnerEditButton != null)
        {
            PartnerEditButton.IsEnabled = _selectedPartner != null;
        }

        var selectedOrder = OrdersGrid.SelectedItem as Order;
        var canChangeOrder = selectedOrder is { Status: not OrderStatus.Shipped and not OrderStatus.Cancelled };
        if (OrdersEditButton != null)
        {
            OrdersEditButton.IsEnabled = canChangeOrder;
        }

        if (OrdersCancelButton != null)
        {
            OrdersCancelButton.IsEnabled = canChangeOrder;
        }

        if (KmDeleteBatchButton != null)
        {
            KmDeleteBatchButton.IsEnabled = _adminDeleteModeEnabled && KmBatchesGrid.SelectedItem is KmCodeBatch;
        }
    }

    private bool EnsureDeleteModeEnabled(string caption)
    {
        if (_adminDeleteModeEnabled)
        {
            return true;
        }

        MessageBox.Show(
            "Удаление записей заблокировано. Включите режим удаления через Сервис -> Администрирование.",
            caption,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private void TryLoadAllOnStartup()
    {
        try
        {
            if (!TryCheckServerApiAvailable(out var serverApiError))
            {
                _serverApiUnavailableAtStartup = true;
                _services.AppLogger.Warn($"FlowStock Server API unavailable at startup: {serverApiError}");
                LoadLowStockView(new Dictionary<long, LowStockSnapshot>());
                UpdateStockEmptyState(null);
                MessageBox.Show(
                    "FlowStock Server API недоступен, поэтому данные не загружены." +
                    Environment.NewLine +
                    Environment.NewLine +
                    $"Проверьте адрес сервера в настройках: {GetConfiguredServerBaseUrl()}" +
                    Environment.NewLine +
                    $"Техническая ошибка: {serverApiError}",
                    "FlowStock",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            LoadAll();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Initial data load failed", ex);
            var message = DatabaseErrorFormatter.IsSchemaIssue(ex)
                ? DatabaseErrorFormatter.Format(ex)
                : "Не удалось подключиться к БД при запуске. Приложение открыто, но часть данных недоступна." +
                  Environment.NewLine +
                  Environment.NewLine +
                  "Проверьте настройки в меню: Сервис -> Администрирование -> Подключение к БД." +
                  Environment.NewLine +
                  $"Техническая ошибка: {ex.Message}";
            MessageBox.Show(message, "FlowStock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadAll()
    {
        LoadItemTypes();
        LoadItems();
        LoadUoms();
        LoadTaras();
        LoadLocations();
        LoadPartners();
        LoadDocs();
        LoadOrders();
        LoadStock(null);
        LoadLowStockView();
        UpdateItemRequestsBadge();
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_serverApiUnavailableAtStartup)
        {
            return;
        }

        _autoRefreshTimer.Start();
    }

    private bool TryCheckServerApiAvailable(out string error)
    {
        error = string.Empty;
        try
        {
            using var handler = new HttpClientHandler();
            if (IsInvalidTlsAllowed())
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(GetConfiguredServerBaseUrl(), UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(2)
            };
            using var response = client.GetAsync("/api/version", HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            error = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string GetConfiguredServerBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return FlowStockUrlHelper.NormalizeRootUrlOrDefault(
                env,
                FlowStockEndpointDefaults.ServerBaseUrl,
                Uri.UriSchemeHttps);
        }

        return _services.Settings.Load().Server.GetServerBaseUrlOrDefault();
    }

    private bool IsInvalidTlsAllowed()
    {
        var env = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim().ToLowerInvariant() switch
            {
                "1" => true,
                "true" => true,
                "yes" => true,
                "on" => true,
                "0" => false,
                "false" => false,
                "no" => false,
                "off" => false,
                _ => _services.Settings.Load().Server.AllowInvalidTls
            };
        }

        return _services.Settings.Load().Server.AllowInvalidTls;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _autoRefreshTimer.Stop();
    }

    private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshActiveTab();
    }

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!ReferenceEquals(e.Source, MainTabs))
        {
            return;
        }

        RefreshActiveTab();
    }

    private void RefreshActiveTab()
    {
        if (_autoRefreshInProgress)
        {
            return;
        }

        _autoRefreshInProgress = true;
        try
        {
            switch (MainTabs.SelectedIndex)
            {
                case TabStatusIndex:
                    LoadItemTypes();
                    LoadStock(StatusSearchBox.Text, debounce: true);
                    break;
                case TabProductionNeedIndex:
                    LoadProductionNeedRows();
                    break;
                case TabDocsIndex:
                    LoadDocs();
                    break;
                case TabOrdersIndex:
                    RefreshOrdersKeepingPagedDepth();
                    break;
                case TabTasksIndex:
                    if (ExperimentalFeatureFlags.WarehouseTasksEnabled)
                    {
                        LoadWarehouseBundles();
                    }
                    break;
                case TabItemsIndex:
                    LoadItems(ItemsSearchBox?.Text);
                    break;
                case TabLocationsIndex:
                    LoadLocations();
                    break;
                case TabPartnersIndex:
                    LoadPartners();
                    break;
                case TabKmIndex:
                    LoadKmBatches();
                    break;
            }

            UpdateItemRequestsBadge();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Auto refresh failed", ex);
        }
        finally
        {
            _autoRefreshInProgress = false;
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            if (MainTabs.SelectedIndex == TabItemsIndex
                && ItemsGrid.IsKeyboardFocusWithin
                && ItemsGrid.SelectedItems.Count > 0)
            {
                e.Handled = true;
                DeleteItem_Click(ItemsGrid, new RoutedEventArgs());
            }

            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                e.Handled = true;
                ShowNewDocDialog();
                break;
            case Key.O:
                e.Handled = true;
                OpenSelectedDoc();
                break;
            case Key.Enter:
                e.Handled = true;
                await TryCloseSelectedDocAsync();
                break;
        }
    }

    private void LoadItems(string? search = null)
    {
        var selectedId = _selectedItem?.Id;
        _items.Clear();
        var query = search ?? ItemsSearchBox?.Text;
        var normalized = NormalizeIdentifier(query);
        var items = _services.WpfReadApi.TryGetItems(normalized, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        foreach (var item in items)
        {
            _items.Add(item);
        }
        RestoreItemSelection(selectedId);
    }

    private void LoadUoms()
    {
        _uoms.Clear();
        var uoms = _services.WpfCatalogApi.TryGetUoms(out var apiUoms)
            ? apiUoms
            : Array.Empty<Uom>();
        foreach (var uom in uoms)
        {
            _uoms.Add(uom);
        }
    }

    private void LoadTaras()
    {
        _taras.Clear();
        var taras = _services.WpfCatalogApi.TryGetTaras(out var apiTaras)
            ? apiTaras
            : Array.Empty<Tara>();
        foreach (var tara in taras)
        {
            _taras.Add(tara);
        }
    }

    private void LoadItemTypes()
    {
        var selectedId = GetSelectedStockItemTypeId();
        _suppressStockFilterSelectionChanged = true;
        try
        {
            _stockItemTypeFilters.Clear();
            _stockItemTypeFilters.Add(new StockItemTypeFilterOption(null, "Все типы"));

            var itemTypes = _services.WpfCatalogApi.TryGetItemTypes(includeInactive: false, out var apiItemTypes)
                ? apiItemTypes
                : Array.Empty<ItemType>();
            foreach (var itemType in itemTypes.OrderBy(type => type.SortOrder).ThenBy(type => type.Name))
            {
                _stockItemTypeFilters.Add(new StockItemTypeFilterOption(itemType.Id, itemType.Name));
            }

            var selected = _stockItemTypeFilters.FirstOrDefault(option => option.Id == selectedId)
                           ?? _stockItemTypeFilters.FirstOrDefault();
            StockItemTypeFilter.SelectedItem = selected;
        }
        finally
        {
            _suppressStockFilterSelectionChanged = false;
        }
    }

    private void LoadLocations()
    {
        var selectedId = _selectedLocation?.Id;
        _locations.Clear();
        var locations = _services.WpfReadApi.TryGetLocations(out var apiLocations)
            ? apiLocations
            : Array.Empty<Location>();
        foreach (var location in locations)
        {
            _locations.Add(location);
        }

        LoadStockLocationFilters();
        LoadStockHuFilters();
        RestoreLocationSelection(selectedId);
    }

    private void LoadStockLocationFilters()
    {
        var selectedCode = GetSelectedStockLocationCode();
        _suppressStockFilterSelectionChanged = true;
        try
        {
            _stockLocationFilters.Clear();
            _stockLocationFilters.Add(new StockLocationFilterOption(null, "Все места"));
            foreach (var location in _locations)
            {
                _stockLocationFilters.Add(new StockLocationFilterOption(location.Code, location.DisplayName));
            }

            var selected = _stockLocationFilters.FirstOrDefault(option => string.Equals(option.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
                           ?? _stockLocationFilters.FirstOrDefault();
            StockLocationFilter.SelectedItem = selected;
        }
        finally
        {
            _suppressStockFilterSelectionChanged = false;
        }
    }

    private void LoadStockHuFilters(IReadOnlyList<StockRow>? sourceRows = null)
    {
        var selectedCode = GetSelectedStockHuCode();
        _suppressStockFilterSelectionChanged = true;
        try
        {
            _stockHuFilters.Clear();
            _stockHuFilters.Add(new StockHuFilterOption(null, "Все HU"));

            var availableHuCodes = GetAvailableHuCodesForFilter(sourceRows);
            foreach (var hu in availableHuCodes)
            {
                _stockHuFilters.Add(new StockHuFilterOption(hu, hu));
            }

            var selected = _stockHuFilters.FirstOrDefault(option => string.Equals(option.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
                           ?? _stockHuFilters.FirstOrDefault();
            StockHuFilter.SelectedItem = selected;
        }
        finally
        {
            _suppressStockFilterSelectionChanged = false;
        }
    }

    private IEnumerable<string> GetAvailableHuCodesForFilter(IReadOnlyList<StockRow>? sourceRows = null)
    {
        var locationCode = GetSelectedStockLocationCode();
        var rows = sourceRows ?? (_services.WpfReadApi.TryGetStockRows(null, out var apiRows)
            ? apiRows
            : Array.Empty<StockRow>());
        return (string.IsNullOrWhiteSpace(locationCode)
                ? rows
                : rows.Where(row => string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase)))
            .Select(row => row.Hu?.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadPartners()
    {
        var selectedId = _selectedPartner?.Id;
        _partners.Clear();
        if (_services.WpfPartnerApi.TryGetPartners(out var apiPartners))
        {
            foreach (var entry in apiPartners)
            {
                _partners.Add(new PartnerRow(entry.Partner, GetPartnerStatusLabel(entry.Status)));
            }
        }
        else
        {
            var partners = _services.WpfReadApi.TryGetPartners(out var readApiPartners)
                ? readApiPartners
                : Array.Empty<Partner>();
            foreach (var partner in partners)
            {
                _partners.Add(new PartnerRow(partner, string.Empty));
            }
        }
        RestorePartnerSelection(selectedId);
    }

    private void LoadDocs()
    {
        var selectedId = (DocsGrid.SelectedItem as Doc)?.Id;
        _docs.Clear();
        var docs = _services.WpfReadApi.TryGetDocs(
            (DocsTypeFilter.SelectedItem as DocTypeFilterOption)?.Type,
            (DocsStatusFilter.SelectedItem as DocStatusFilterOption)?.Status,
            out var apiDocs)
            ? apiDocs
            : Array.Empty<Doc>();
        foreach (var doc in ApplyDocFilters(docs))
        {
            _docs.Add(doc);
        }
        RestoreDocSelection(selectedId);
    }

    private IEnumerable<Doc> ApplyDocFilters(IReadOnlyList<Doc> docs)
    {
        var query = DocsSearchBox.Text?.Trim() ?? string.Empty;
        var typeFilter = (DocsTypeFilter.SelectedItem as DocTypeFilterOption)?.Type;
        var statusFilter = (DocsStatusFilter.SelectedItem as DocStatusFilterOption)?.Status;

        IEnumerable<Doc> filtered = docs;
        if (typeFilter.HasValue)
        {
            filtered = filtered.Where(doc => doc.Type == typeFilter.Value);
        }

        if (statusFilter.HasValue)
        {
            filtered = filtered.Where(doc => doc.Status == statusFilter.Value);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(doc => DocMatchesQuery(doc, query));
        }

        return filtered;
    }

    private static bool DocMatchesQuery(Doc doc, string query)
    {
        return Contains(doc.DocRef, query)
               || Contains(doc.PartnerName, query)
               || Contains(doc.PartnerCode, query)
               || Contains(doc.OrderRef, query)
               || Contains(doc.TypeDisplay, query);
    }

    private static bool Contains(string? source, string query)
    {
        return !string.IsNullOrWhiteSpace(source)
               && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void LoadOrders(bool reset = true)
    {
        var selectedId = (OrdersGrid.SelectedItem as Order)?.Id;
        if (reset)
        {
            _orders.Clear();
        }

        var offset = reset ? 0 : _orders.Count;
        var includeCancelledMerged = ShowCancelledMergedOrdersCheckBox.IsChecked == true;
        var page = _services.WpfReadApi.TryGetOrdersPage(
            includeInternal: true,
            search: null,
            limit: OrdersPageSize,
            offset: offset,
            includeCancelledMerged: includeCancelledMerged,
            out var apiOrders)
            ? apiOrders
            : Array.Empty<Order>();
        foreach (var order in page)
        {
            _orders.Add(order);
        }

        _ordersHasMore = page.Count >= OrdersPageSize;
        _ordersPagedDepth = _orders.Count;
        UpdateLoadMoreOrdersButton();
        if (reset)
        {
            RestoreOrderSelection(selectedId);
        }

        UpdateDeleteButtonsAvailability();
    }

    private void RefreshOrdersKeepingPagedDepth()
    {
        var selectedId = (OrdersGrid.SelectedItem as Order)?.Id;
        var targetCount = Math.Max(_ordersPagedDepth, OrdersPageSize);
        var includeCancelledMerged = ShowCancelledMergedOrdersCheckBox.IsChecked == true;

        _orders.Clear();
        var offset = 0;
        IReadOnlyList<Order> lastPage = Array.Empty<Order>();
        while (_orders.Count < targetCount)
        {
            if (!_services.WpfReadApi.TryGetOrdersPage(
                    includeInternal: true,
                    search: null,
                    limit: OrdersPageSize,
                    offset: offset,
                    includeCancelledMerged: includeCancelledMerged,
                    out var apiOrders))
            {
                break;
            }

            lastPage = apiOrders;
            if (lastPage.Count == 0)
            {
                break;
            }

            foreach (var order in lastPage)
            {
                _orders.Add(order);
            }

            offset += lastPage.Count;
            if (lastPage.Count < OrdersPageSize)
            {
                break;
            }
        }

        _ordersHasMore = lastPage.Count >= OrdersPageSize;
        _ordersPagedDepth = _orders.Count;
        UpdateLoadMoreOrdersButton();
        RestoreOrderSelection(selectedId);
        UpdateDeleteButtonsAvailability();
    }

    private void ShowCancelledMergedOrdersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        LoadOrders(reset: true);
    }

    private void LoadMoreOrders_Click(object sender, RoutedEventArgs e)
    {
        LoadOrders(reset: false);
    }

    private void UpdateLoadMoreOrdersButton()
    {
        if (_ordersHasMore)
        {
            LoadMoreOrdersButton.Visibility = Visibility.Visible;
            LoadMoreOrdersButton.IsEnabled = true;
            LoadMoreOrdersButton.Content = "Загрузить следующие";
            return;
        }

        if (_orders.Count > 0)
        {
            LoadMoreOrdersButton.Visibility = Visibility.Visible;
            LoadMoreOrdersButton.IsEnabled = false;
            LoadMoreOrdersButton.Content = "Больше заказов нет";
            return;
        }

        LoadMoreOrdersButton.Visibility = Visibility.Collapsed;
    }

    private void LoadStock(string? search, bool debounce = false)
    {
        if (debounce)
        {
            _pendingStockSearch = search;
            _stockRefreshDebounceTimer ??= new DispatcherTimer { Interval = StockRefreshDebounceInterval };
            if (!_stockRefreshDebounceTickAttached)
            {
                _stockRefreshDebounceTimer.Tick += (_, _) =>
                {
                    _stockRefreshDebounceTimer!.Stop();
                    LoadStock(_pendingStockSearch);
                };
                _stockRefreshDebounceTickAttached = true;
            }

            _stockRefreshDebounceTimer.Stop();
            _stockRefreshDebounceTimer.Start();
            return;
        }

        LoadWarehouseProductionState(search);
        LoadProductionNeedRows();
    }

    private void LoadWarehouseProductionState(string? search)
    {
        var belowMinOnly = StockBelowMinOnlyCheckBox.IsChecked == true;
        var itemTypeId = GetSelectedStockItemTypeId();
        if (!_services.WpfReadApi.TryGetWarehouseProductionStateRows(
                includeZero: false,
                search,
                belowMinOnly,
                out var rows))
        {
            _warehouseProductionStateFingerprint = null;
            _warehouseProductionStateRows.Clear();
            UpdateStockEmptyState(search);
            StockEmptyText.Text = "Не удалось загрузить производственный dashboard. Проверьте доступность FlowStock Server API.";
            LowStockGrid.Visibility = Visibility.Collapsed;
            LowStockSummaryText.Text = string.Empty;
            return;
        }

        var itemTypeByItemId = (_services.WpfReadApi.TryGetItems(null, out var apiItems) ? apiItems : Array.Empty<Item>())
            .ToDictionary(item => item.Id, item => item.ItemTypeId);
        var locationCode = GetSelectedStockLocationCode();
        var huCode = GetSelectedStockHuCode();
        var filteredRows = rows
            .Where(row => !itemTypeId.HasValue
                          || itemTypeByItemId.TryGetValue(row.ItemId, out var currentItemTypeId)
                          && currentItemTypeId == itemTypeId.Value)
            .Where(row => string.IsNullOrWhiteSpace(locationCode)
                          || row.HuRows.Any(hu => string.Equals(hu.Location, locationCode, StringComparison.OrdinalIgnoreCase)))
            .Where(row => string.IsNullOrWhiteSpace(huCode)
                          || row.HuRows.Any(hu => string.Equals(hu.HuCode, huCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var fingerprint = BuildWarehouseProductionStateFingerprint(filteredRows);
        if (string.Equals(fingerprint, _warehouseProductionStateFingerprint, StringComparison.Ordinal))
        {
            UpdateStockEmptyState(search);
            return;
        }

        _warehouseProductionStateFingerprint = fingerprint;
        var selectedItemId = (WarehouseProductionStateGrid.SelectedItem as WarehouseProductionStateDisplayRow)?.ItemId;
        var scrollOffset = GetDataGridVerticalScrollOffset(WarehouseProductionStateGrid);
        var existingByItemId = _warehouseProductionStateRows.ToDictionary(row => row.ItemId);
        var nextRows = new List<WarehouseProductionStateDisplayRow>(filteredRows.Count);
        foreach (var row in filteredRows)
        {
            if (existingByItemId.TryGetValue(row.ItemId, out var existing))
            {
                existing.ApplyFrom(row);
                nextRows.Add(existing);
                continue;
            }

            nextRows.Add(CreateWarehouseProductionStateDisplayRow(row));
        }

        for (var index = _warehouseProductionStateRows.Count - 1; index >= 0; index--)
        {
            var itemId = _warehouseProductionStateRows[index].ItemId;
            if (nextRows.All(row => row.ItemId != itemId))
            {
                _warehouseProductionStateRows.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < nextRows.Count; targetIndex++)
        {
            var desiredRow = nextRows[targetIndex];
            var currentIndex = _warehouseProductionStateRows.IndexOf(desiredRow);
            if (currentIndex < 0)
            {
                _warehouseProductionStateRows.Insert(targetIndex, desiredRow);
                continue;
            }

            if (currentIndex != targetIndex)
            {
                _warehouseProductionStateRows.Move(currentIndex, targetIndex);
            }
        }

        UpdateStockEmptyState(search);
        StockGrid.Visibility = Visibility.Collapsed;
        WarehouseProductionStateGrid.Visibility = Visibility.Visible;
        LowStockGrid.Visibility = Visibility.Collapsed;
        LowStockPanel.Visibility = Visibility.Collapsed;
        LowStockSummaryText.Text = string.Empty;
        RestoreWarehouseProductionStateGridViewState(selectedItemId, scrollOffset);
    }

    private WarehouseProductionStateDisplayRow CreateWarehouseProductionStateDisplayRow(WarehouseProductionStateRow row)
    {
        var displayRow = new WarehouseProductionStateDisplayRow { ItemId = row.ItemId };
        displayRow.ApplyFrom(row);
        displayRow.IsExpanded = _expandedStockItemIds.Contains(row.ItemId);
        displayRow.ExpandMarker = displayRow.IsExpanded ? "▼" : "▶";
        return displayRow;
    }

    private static string BuildWarehouseProductionStateFingerprint(IReadOnlyList<WarehouseProductionStateRow> rows)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(rows.Count * 48);
        foreach (var row in rows.OrderBy(current => current.ItemId))
        {
            builder.Append(row.ItemId)
                .Append('|').Append(row.StockQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.FreeQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.ReservedQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.MinStockQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.BelowMinQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.CustomerOpenDemandQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.PrdPlannedQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.PrdFilledQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.InternalRemainingQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.RemainingNeedQty.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.HuRows.Count)
                .Append('|').Append(row.ProductionReceipts.Count)
                .Append('|').Append(row.NeedBreakdown.DemandToCloseCustomerOrders.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.NeedBreakdown.DemandToMinStock.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.NeedBreakdown.AlreadyPlannedInternal.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.NeedBreakdown.RemainingToCreate.ToString("F3", CultureInfo.InvariantCulture))
                .Append(';');
        }

        return builder.ToString();
    }

    private static double? GetDataGridVerticalScrollOffset(System.Windows.Controls.DataGrid grid)
    {
        var scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(grid);
        return scrollViewer?.VerticalOffset;
    }

    private void RestoreWarehouseProductionStateGridViewState(long? selectedItemId, double? scrollOffset)
    {
        if (selectedItemId.HasValue)
        {
            var selectedRow = _warehouseProductionStateRows.FirstOrDefault(row => row.ItemId == selectedItemId.Value);
            if (selectedRow != null)
            {
                WarehouseProductionStateGrid.SelectedItem = selectedRow;
            }
        }

        if (!scrollOffset.HasValue)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(WarehouseProductionStateGrid);
            scrollViewer?.ScrollToVerticalOffset(scrollOffset.Value);
        }), DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private Dictionary<string, HuStockContextRow> BuildHuContextMap()
    {
        if (!_services.WpfReadApi.TryGetHuStockRows(out var rows))
        {
            return new Dictionary<string, HuStockContextRow>(StringComparer.OrdinalIgnoreCase);
        }

        return rows
            .Where(row => row.ItemId > 0 && !string.IsNullOrWhiteSpace(row.Hu))
            .GroupBy(row => BuildHuContextKey(row.ItemId, row.Hu))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveOriginOrderDisplay(StockRow row, IReadOnlyDictionary<string, HuStockContextRow> contextMap)
    {
        if (!row.ItemTypeEnableOrderReservation)
        {
            return "—";
        }

        var context = TryGetHuContext(row, contextMap);
        return string.IsNullOrWhiteSpace(context?.OriginInternalOrderRef)
            ? "—"
            : context.OriginInternalOrderRef!;
    }

    private static string ResolveReservedOrderDisplay(StockRow row, IReadOnlyDictionary<string, HuStockContextRow> contextMap)
    {
        if (!row.ItemTypeEnableOrderReservation)
        {
            return "—";
        }

        var context = TryGetHuContext(row, contextMap);
        return string.IsNullOrWhiteSpace(context?.ReservedCustomerOrderRef)
            ? "не зарезервировано"
            : context.ReservedCustomerOrderRef!;
    }

    private static string ResolveReservedCustomerDisplay(StockRow row, IReadOnlyDictionary<string, HuStockContextRow> contextMap)
    {
        if (!row.ItemTypeEnableOrderReservation)
        {
            return "—";
        }

        var context = TryGetHuContext(row, contextMap);
        return string.IsNullOrWhiteSpace(context?.ReservedCustomerName)
            ? "не зарезервировано"
            : context.ReservedCustomerName!;
    }

    private static HuStockContextRow? TryGetHuContext(StockRow row, IReadOnlyDictionary<string, HuStockContextRow> contextMap)
    {
        if (string.IsNullOrWhiteSpace(row.Hu))
        {
            return null;
        }

        return contextMap.TryGetValue(BuildHuContextKey(row.ItemId, row.Hu), out var context) ? context : null;
    }

    private static string BuildHuContextKey(long itemId, string huCode)
    {
        return $"{itemId}|{huCode.Trim().ToUpperInvariant()}";
    }

    private void UpdateStockEmptyState(string? search)
    {
        var currentCount = _warehouseProductionStateRows.Count;
        if (string.IsNullOrWhiteSpace(search)
            && currentCount == 0
            && string.IsNullOrWhiteSpace(GetSelectedStockLocationCode())
            && string.IsNullOrWhiteSpace(GetSelectedStockHuCode())
            && !GetSelectedStockItemTypeId().HasValue
            && StockBelowMinOnlyCheckBox.IsChecked != true)
        {
            StockEmptyText.Text = "Нет позиций по остаткам и производственной потребности.";
            StockEmptyText.Visibility = Visibility.Visible;
            return;
        }

        StockEmptyText.Visibility = Visibility.Collapsed;
    }

    private Dictionary<long, LowStockSnapshot> BuildLowStockByItem(IReadOnlyList<StockRow> rows, IReadOnlyList<Item> allItems)
    {
        var itemsPresentInStock = rows
            .Select(row => row.ItemId)
            .ToHashSet();

        var snapshots = rows
            .GroupBy(row => row.ItemId)
            .Select(group =>
            {
                var first = group.First();
                var totalQty = group.Sum(row => row.Qty);
                var qtyForMinControl = first.ItemTypeMinStockUsesOrderBinding
                    ? first.AvailableForMinStockQty
                    : totalQty;
                var minStockQty = first.MinStockQty;
                var isBelow = first.ItemTypeEnableMinStockControl
                              && minStockQty.HasValue
                              && qtyForMinControl < minStockQty.Value;
                return new LowStockSnapshot(
                    group.Key,
                    first.ItemName,
                    first.ItemTypeName ?? "Без типа",
                    first.BaseUom,
                    qtyForMinControl,
                    minStockQty,
                    isBelow);
            })
            .Where(snapshot => snapshot.IsBelowMin)
            .ToDictionary(snapshot => snapshot.ItemId, snapshot => snapshot);

        foreach (var item in allItems)
        {
            if (itemsPresentInStock.Contains(item.Id))
            {
                continue;
            }

            if (snapshots.ContainsKey(item.Id))
            {
                continue;
            }

            var minStockQty = item.MinStockQty;
            var isBelow = item.ItemTypeEnableMinStockControl
                          && minStockQty.HasValue
                          && 0d < minStockQty.Value;
            if (!isBelow)
            {
                continue;
            }

            snapshots[item.Id] = new LowStockSnapshot(
                item.Id,
                item.Name,
                item.ItemTypeName ?? "Без типа",
                item.BaseUom,
                0d,
                minStockQty,
                true);
        }

        return snapshots;
    }

    private void LoadLowStockView(Dictionary<long, LowStockSnapshot>? precomputed = null)
    {
        _lowStock.Clear();
        var lowStockByItem = precomputed;
        if (lowStockByItem == null)
        {
            var rows = _services.WpfReadApi.TryGetStockRows(null, out var apiRows)
                ? apiRows
                : Array.Empty<StockRow>();
            var allItems = _services.WpfReadApi.TryGetItems(null, out var apiItems)
                ? apiItems
                : Array.Empty<Item>();
            lowStockByItem = BuildLowStockByItem(rows, allItems);
        }
        var belowMinRows = lowStockByItem
            .Values
            .OrderBy(snapshot => snapshot.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var snapshot in belowMinRows)
        {
            var shortage = snapshot.MinStockQty.GetValueOrDefault() - snapshot.Qty;
            _lowStock.Add(new LowStockDisplayRow
            {
                ItemName = snapshot.ItemName,
                ItemTypeName = snapshot.ItemTypeName,
                QtyDisplay = FormatQtyWithUom(snapshot.Qty, snapshot.BaseUom),
                MinStockQtyDisplay = FormatQtyWithUom(snapshot.MinStockQty.GetValueOrDefault(), snapshot.BaseUom),
                ShortageDisplay = FormatQtyWithUom(shortage > 0 ? shortage : 0, snapshot.BaseUom)
            });
        }

        LowStockSummaryText.Text = belowMinRows.Count == 0
            ? "Позиции ниже минимума отсутствуют."
            : $"Позиции ниже минимума: {belowMinRows.Count}";
        UpdateLowStockIndicator(lowStockByItem);
    }

    private void UpdateLowStockIndicator(IReadOnlyDictionary<long, LowStockSnapshot> lowStockByItem)
    {
        if (LowStockIndicatorText == null)
        {
            return;
        }

        var count = lowStockByItem.Count;
        if (count <= 0)
        {
            LowStockIndicatorText.Text = string.Empty;
            LowStockIndicatorText.Visibility = Visibility.Collapsed;
            return;
        }

        LowStockIndicatorText.Text = $"Позиции ниже минимума: {count}";
        LowStockIndicatorText.Visibility = Visibility.Visible;
    }

    private void StatusSearch_Click(object sender, RoutedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private void ItemsSearch_Click(object sender, RoutedEventArgs e)
    {
        LoadItems(ItemsSearchBox?.Text);
    }

    private void ItemsResetSearch_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsSearchBox != null)
        {
            ItemsSearchBox.Text = string.Empty;
        }
        LoadItems(null);
    }

    private void ItemsSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LoadItems(ItemsSearchBox?.Text);
        }
    }

    private void StockLocationFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressStockFilterSelectionChanged)
        {
            return;
        }

        LoadStockHuFilters();
        LoadStock(StatusSearchBox.Text);
    }

    private void StockHuFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressStockFilterSelectionChanged)
        {
            return;
        }

        LoadStock(StatusSearchBox.Text);
    }

    private void StockItemTypeFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressStockFilterSelectionChanged)
        {
            return;
        }

        LoadStock(StatusSearchBox.Text);
    }

    private void StockBelowMinOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private void UpdateStockModeUi()
    {
        StockGrid.Visibility = Visibility.Collapsed;
        WarehouseProductionStateGrid.Visibility = Visibility.Visible;
        LowStockGrid.Visibility = Visibility.Collapsed;
        LowStockPanel.Visibility = Visibility.Collapsed;
        LowStockSummaryText.Text = string.Empty;
    }

    private void ProductionNeedRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadProductionNeedRows(showErrorMessage: true);
    }

    private async void ProductionNeedCreateOrders_Click(object sender, RoutedEventArgs e)
    {
        ProductionNeedCreateOrdersButton.IsEnabled = false;
        ProductionNeedSummaryText.Text = "Подготовка предпросмотра...";

        try
        {
            var preview = await _services.WpfReadApi.GetProductionNeedOrderPreviewAsync();
            if (!preview.IsSuccess)
            {
                ProductionNeedSummaryText.Text = "Не удалось получить предпросмотр.";
                MessageBox.Show(
                    preview.ErrorMessage,
                    "Потребность производства",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (preview.Rows.Count == 0)
            {
                ProductionNeedSummaryText.Text = "Нет позиций для создания внутреннего заказа.";
                return;
            }

            var previewWindow = new ProductionNeedDraftPreviewWindow(preview.Rows)
            {
                Owner = this
            };
            if (previewWindow.ShowDialog() != true)
            {
                ProductionNeedSummaryText.Text = "Создание отменено.";
                return;
            }

            var requestLines = previewWindow.GetConfirmedLines();
            if (requestLines.Count == 0)
            {
                ProductionNeedSummaryText.Text = "Нет позиций для создания внутреннего заказа.";
                return;
            }

            ProductionNeedSummaryText.Text = "Формирование производственного черновика...";
            var result = await _services.WpfReadApi.CreateProductionNeedOrdersAsync(
                requestLines.Select(line => new ProductionNeedOrderDraftRequestLine
                {
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                }).ToArray());
            if (!result.IsSuccess)
            {
                ProductionNeedSummaryText.Text = "Не удалось сформировать производственный черновик.";
                MessageBox.Show(
                    result.ErrorMessage,
                    "Потребность производства",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(
                result.Message,
                "Потребность производства",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            LoadStock(StatusSearchBox.Text);
        }
        finally
        {
            LoadProductionNeedRows();
        }
    }

    private void LoadProductionNeedRows(bool showErrorMessage = false)
    {
        if (!_services.WpfReadApi.TryGetProductionNeedRows(
                includeZeroNeed: false,
                out var rows))
        {
            _productionNeedRows.Clear();
            ProductionNeedCreateOrdersButton.IsEnabled = false;
            ProductionNeedSummaryText.Text = "Не удалось загрузить отчет. Проверьте доступность FlowStock Server API.";
            if (showErrorMessage)
            {
                MessageBox.Show(
                    "Не удалось загрузить отчет потребности производства. Проверьте доступность FlowStock Server API.",
                    "Потребность производства",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        _productionNeedRows.Clear();
        foreach (var row in rows)
        {
            _productionNeedRows.Add(new ProductionNeedDisplayRow
            {
                ItemId = row.ItemId,
                Gtin = string.IsNullOrWhiteSpace(row.Gtin) ? "-" : row.Gtin,
                ItemName = row.ItemName,
                ItemTypeName = string.IsNullOrWhiteSpace(row.ItemTypeName) ? "Без типа" : row.ItemTypeName,
                FreeStockQty = row.FreeStockQty,
                MinStockQty = row.MinStockQty,
                ToCloseOrdersQty = row.ToCloseOrdersQty,
                ToMinStockQty = row.ToMinStockQty,
                OpenInternalOrderQty = row.OpenInternalOrderQty,
                OpenInternalOrderRefs = row.OpenInternalOrderRefs,
                PlannedPalletQty = row.PlannedPalletQty,
                FilledPalletQty = row.FilledPalletQty,
                PlannedPalletCount = row.PlannedPalletCount,
                FilledPalletCount = row.FilledPalletCount,
                RemainingPalletQty = row.RemainingPalletQty,
                QtyToCreate = row.QtyToCreate,
                CanCreateOrder = row.CanCreateOrder,
                Reason = row.Reason,
                TotalToMakeQty = row.TotalToMakeQty
            });
        }

        var creatableCount = _productionNeedRows.Count(row => row.CanCreateOrder && row.QtyToCreate > 0.000001d);
        ProductionNeedCreateOrdersButton.IsEnabled = creatableCount > 0;
        ProductionNeedSummaryText.Text = $"Позиций: {_productionNeedRows.Count}. К созданию: {creatableCount}.";
    }

    private string? GetSelectedStockLocationCode()
    {
        return (StockLocationFilter.SelectedItem as StockLocationFilterOption)?.Code;
    }

    private string? GetSelectedStockHuCode()
    {
        return (StockHuFilter.SelectedItem as StockHuFilterOption)?.Code;
    }

    private void StockGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ToggleStockRowDetails<StockDisplayRow>(StockGrid, e);
    }

    private void WarehouseProductionStateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ToggleStockRowDetails<WarehouseProductionStateDisplayRow>(WarehouseProductionStateGrid, e);
    }

    private void ToggleStockRowDetails<TRow>(System.Windows.Controls.DataGrid gridControl, MouseButtonEventArgs e)
        where TRow : IExpandableStockRow
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var sourceGrid = FindVisualParent<System.Windows.Controls.DataGrid>(source);
        if (!ReferenceEquals(sourceGrid, gridControl))
        {
            // Ignore double-clicks inside row details nested grids.
            return;
        }

        var clickedRow = FindVisualParent<System.Windows.Controls.DataGridRow>(source);
        if (clickedRow?.DataContext is not TRow row)
        {
            return;
        }

        e.Handled = true;
        var nextExpanded = !row.IsExpanded;
        if (nextExpanded)
        {
            _expandedStockItemIds.Add(row.ItemId);
        }
        else
        {
            _expandedStockItemIds.Remove(row.ItemId);
        }

        row.IsExpanded = nextExpanded;
        row.ExpandMarker = nextExpanded ? "▼" : "▶";
        clickedRow.DetailsVisibility = nextExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T matched)
            {
                return matched;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ApplyExpandedStockRowDetailsVisibility()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var row in _stock)
            {
                if (StockGrid.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.Controls.DataGridRow gridRow)
                {
                    continue;
                }

                gridRow.DetailsVisibility = row.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var row in _warehouseProductionStateRows)
            {
                if (WarehouseProductionStateGrid.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.Controls.DataGridRow gridRow)
                {
                    continue;
                }

                gridRow.DetailsVisibility = row.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }
        }), DispatcherPriority.Background);
    }

    private long? GetSelectedStockItemTypeId()
    {
        return (StockItemTypeFilter.SelectedItem as StockItemTypeFilterOption)?.Id;
    }

    private void DocsApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        LoadDocs();
    }

    private void DocsResetFilters_Click(object sender, RoutedEventArgs e)
    {
        DocsSearchBox.Text = string.Empty;
        DocsTypeFilter.SelectedIndex = 0;
        DocsStatusFilter.SelectedIndex = 0;
        LoadDocs();
    }

    private void DocsSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LoadDocs();
        }
    }

    private void DocsOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedDoc();
    }

    private void OpenSelectedDoc()
    {
        if (DocsGrid.SelectedItem is not Doc doc)
        {
            MessageBox.Show("Выберите операцию.", "Операции", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenDocDetails(doc);
    }

    private void OpenDocDetails(Doc doc, string? createdDraftDocUid = null)
    {
        try
        {
            var wasClosed = doc.Status == DocStatus.Closed;
            var window = new OperationDetailsWindow(_services, doc.Id, createdDraftDocUid)
            {
                Owner = this
            };
            window.ShowDialog();

            LoadDocs();
            var refreshed = _services.WpfReadApi.TryGetDoc(doc.Id, out var apiDoc) ? apiDoc : null;
            if (!wasClosed && refreshed?.Status == DocStatus.Closed)
            {
                LoadStock(StatusSearchBox.Text);
                if (refreshed.Type == DocType.Outbound || refreshed.Type == DocType.ProductionReceipt)
                {
                    LoadOrders();
                }
            }
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error($"Open doc details failed for doc_id={doc.Id}", ex);
            MessageBox.Show(DatabaseErrorFormatter.Format(ex), "Операции", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenSelectedDoc();
    }

    private void DocsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedDoc();
        }
    }

    private void OrdersNew_Click(object sender, RoutedEventArgs e)
    {
        var window = new OrderDetailsWindow(_services);
        window.Owner = this;
        window.ShowDialog();
        LoadOrders();
    }

    private void OrdersEdit_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedOrder();
    }

    private void OrdersCancel_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not Order order)
        {
            MessageBox.Show("Выберите заказ.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            MessageBox.Show("Этот заказ уже находится в конечном статусе.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Отменить заказ {order.OrderRef}? Резерв по заказу будет снят, сам заказ останется в истории.",
            "Заказы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = _services.WpfSetOrderStatuses.SetStatusAsync(order.Id, OrderStatus.Cancelled)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!result.IsSuccess)
            {
                var icon = result.Kind is WpfSetOrderStatusResultKind.Timeout or WpfSetOrderStatusResultKind.ServerUnavailable
                    ? MessageBoxImage.Error
                    : MessageBoxImage.Warning;
                MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, icon);
                return;
            }

            LoadDocs();
            LoadOrders();
            LoadStock(StatusSearchBox.Text);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OrdersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateDeleteButtonsAvailability();
    }

    private void OrdersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedOrder();
    }

    private void OrdersGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedOrder();
        }
    }

    private void OpenSelectedOrder()
    {
        if (OrdersGrid.SelectedItem is not Order order)
        {
            MessageBox.Show("Выберите заказ.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new OrderDetailsWindow(_services, order.Id);
        window.Owner = this;
        window.ShowDialog();

        LoadOrders();
        LoadStock(StatusSearchBox.Text);
    }

    private async void DocClose_Click(object sender, RoutedEventArgs e)
    {
        await TryCloseSelectedDocAsync();
    }

    private async Task TryCloseSelectedDocAsync()
    {
        if (DocsGrid.SelectedItem is not Doc doc)
        {
            MessageBox.Show("Операция не выбрана.", "Операции", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (doc.Status == DocStatus.Closed)
        {
            MessageBox.Show("Операция уже закрыта.", "Операции", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (doc.IsRecountRequested)
        {
            MessageBox.Show("Операция находится на перерасчете. Дождитесь данных от ТСД.", "Операции", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await TryCloseSelectedDocViaServerAsync(doc);
    }

    private async Task TryCloseSelectedDocViaServerAsync(Doc doc)
    {
        var result = await _services.WpfCloseDocuments.CloseAsync(doc);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Message, "Операции", MessageBoxButton.OK, ResolveServerCloseMessageImage(result.Kind));
            return;
        }

        RefreshAfterClose(doc.Id);

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Операции", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RefreshAfterClose(long docId)
    {
        LoadDocs();
        LoadStock(StatusSearchBox.Text);

        var refreshed = _services.WpfReadApi.TryGetDoc(docId, out var apiDoc) ? apiDoc : null;
        if (refreshed?.Type is DocType.Outbound or DocType.ProductionReceipt)
        {
            LoadOrders();
        }
    }

    private static MessageBoxImage ResolveServerCloseMessageImage(WpfCloseDocumentResultKind kind)
    {
        return kind switch
        {
            WpfCloseDocumentResultKind.ValidationFailed => MessageBoxImage.Warning,
            WpfCloseDocumentResultKind.NotFound => MessageBoxImage.Warning,
            WpfCloseDocumentResultKind.EventConflict => MessageBoxImage.Warning,
            WpfCloseDocumentResultKind.ServerRejected => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        };
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new ItemEditWindow(_services)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        LoadItems();
        RestoreItemSelection(window.SavedItemId);
    }

    private void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null)
        {
            MessageBox.Show("Выберите товар.", "Товары", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = (_services.WpfReadApi.TryGetItems(null, out var apiItems) ? apiItems : Array.Empty<Item>())
            .FirstOrDefault(item => item.Id == _selectedItem.Id) ?? _selectedItem;
        var window = new ItemEditWindow(_services, current)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        LoadItems();
        RestoreItemSelection(window.SavedItemId ?? _selectedItem.Id);
    }

    private void ImportItems_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel файлы (*.xlsx;*.xls)|*.xlsx;*.xls|Все файлы (*.*)|*.*",
            Title = "Импорт товаров из Excel"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var window = new ItemImportPreviewWindow(_services, dialog.FileName)
        {
            Owner = this
        };

        if (window.ShowDialog() == true && window.ImportSummary != null)
        {
            LoadItems();
            var summary = window.ImportSummary;
            var message =
                "Импорт завершен.\n" +
                $"Создано: {summary.Created}\n" +
                $"Пропущено (дубликаты): {summary.Duplicates}\n" +
                $"Пропущено (пустые строки): {summary.EmptyRows}\n" +
                $"Пропущено (некорректные строки): {summary.InvalidRows}\n" +
                $"Ошибки: {summary.Errors}";
            MessageBox.Show(message, "Товары", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void LoadKmBatches()
    {
        var selectedId = (KmBatchesGrid.SelectedItem as KmCodeBatch)?.Id;
        _kmBatches.Clear();
        foreach (var batch in _services.Km.GetBatches())
        {
            _kmBatches.Add(batch);
        }
        RestoreKmBatchSelection(selectedId);
        UpdateDeleteButtonsAvailability();
    }

    private void RestoreKmBatchSelection(long? batchId)
    {
        if (!batchId.HasValue)
        {
            return;
        }

        var batch = _kmBatches.FirstOrDefault(item => item.Id == batchId.Value);
        if (batch == null)
        {
            return;
        }

        KmBatchesGrid.SelectedItem = batch;
        KmBatchesGrid.ScrollIntoView(batch);
    }

    private void ItemPackaging_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null)
        {
            MessageBox.Show("Выберите товар.", "Товары", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new ItemPackagingWindow(_services, _selectedItem.Id)
        {
            Owner = this
        };
        window.ShowDialog();
        var itemId = _selectedItem.Id;
        LoadItems();
        RestoreItemSelection(itemId);
    }

    private void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        var window = new LocationEditWindow(_services)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        LoadLocations();
        RestoreLocationSelection(window.SavedLocationId);
    }

    private void EditLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocation == null)
        {
            MessageBox.Show("Выберите место хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = (_services.WpfReadApi.TryGetLocations(out var apiLocations) ? apiLocations : Array.Empty<Location>())
            .FirstOrDefault(location => location.Id == _selectedLocation.Id) ?? _selectedLocation;
        var window = new LocationEditWindow(_services, current)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        LoadLocations();
        RestoreLocationSelection(window.SavedLocationId ?? _selectedLocation.Id);
    }

    private void AddPartner_Click(object sender, RoutedEventArgs e)
    {
        var window = new PartnerEditWindow(_services)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        LoadPartners();
        RestorePartnerSelection(window.SavedPartnerId);
    }

    private void EditPartner_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartner == null)
        {
            MessageBox.Show("Выберите контрагента.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = (_services.WpfPartnerApi.TryGetPartners(out var apiPartners)
                ? apiPartners.Select(entry => entry.Partner)
                : _services.WpfReadApi.TryGetPartners(out var apiReadPartners)
                    ? apiReadPartners
                    : Array.Empty<Partner>())
            .FirstOrDefault(p => p.Id == _selectedPartner.Id) ?? _selectedPartner;
        var window = new PartnerEditWindow(_services, current)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        LoadPartners();
        RestorePartnerSelection(window.SavedPartnerId ?? _selectedPartner.Id);
    }

    private void ItemsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedItem = ItemsGrid.SelectedItem as Item;
        UpdateDeleteButtonsAvailability();
    }

    private void RestoreItemSelection(long? itemId)
    {
        if (!itemId.HasValue)
        {
            return;
        }

        var item = _items.FirstOrDefault(i => i.Id == itemId.Value);
        if (item == null)
        {
            return;
        }

        ItemsGrid.SelectedItem = item;
        ItemsGrid.ScrollIntoView(item);
    }

    private void ItemsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;
        DeleteItem_Click(sender, new RoutedEventArgs());
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not Item)
        {
            return;
        }

        EditItem_Click(sender, new RoutedEventArgs());
    }

    private ImportItemsSummary ImportItemsFromExcel(string filePath)
    {
        EnsureExcelEncoding();

        var existingItems = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        var existingCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existingItems)
        {
            AddBarcodeVariants(existingCodes, item.Barcode);
            AddBarcodeVariants(existingCodes, item.Gtin);
        }
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var duplicates = 0;
        var emptyRows = 0;
        var invalidRows = 0;
        var errors = 0;
        var rowIndex = 0;

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            while (reader.Read())
            {
                var code = NormalizeImportedBarcode(ReadExcelCell(reader, 0));
                var name = NormalizeIdentifier(ReadExcelCell(reader, 1));

                if (rowIndex == 0 && IsHeaderRow(code, name))
                {
                    rowIndex++;
                    continue;
                }

                rowIndex++;

                if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
                {
                    emptyRows++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                {
                    invalidRows++;
                    continue;
                }

                if (IsBarcodeSeen(seenCodes, code))
                {
                    duplicates++;
                    continue;
                }

                if (existingCodes.Contains(code))
                {
                    duplicates++;
                    continue;
                }

                try
                {
                    AddBarcodeVariants(seenCodes, code);
                    var gtin = IsDigitsOnly(code) ? code : null;
                    var createdResult = _services.WpfCatalogApi.TryCreateItemAsync(new Item
                        {
                            Name = name,
                            Barcode = code,
                            Gtin = gtin,
                            BaseUom = "шт",
                            IsMarked = false
                        })
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                    if (!createdResult.IsSuccess)
                    {
                        if (string.Equals(createdResult.Error, "ITEM_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
                        {
                            duplicates++;
                            continue;
                        }

                        throw new InvalidOperationException(createdResult.Error ?? "Не удалось создать товар через сервер.");
                    }
                    created++;
                    AddBarcodeVariants(existingCodes, code);
                    AddBarcodeVariants(existingCodes, gtin);
                }
                catch (ArgumentException)
                {
                    invalidRows++;
                }
                catch (PostgresException ex) when (IsPostgresConstraint(ex))
                {
                    duplicates++;
                }
                catch
                {
                    errors++;
                }
            }

            break;
        } while (reader.NextResult());

        return new ImportItemsSummary(created, duplicates, emptyRows, invalidRows, errors);
    }

    private static void EnsureExcelEncoding()
    {
        if (_excelEncodingRegistered)
        {
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _excelEncodingRegistered = true;
    }

    private static string? ReadExcelCell(IExcelDataReader reader, int index)
    {
        if (index < 0 || index >= reader.FieldCount)
        {
            return null;
        }

        var value = reader.GetValue(index);
        if (value == null)
        {
            return null;
        }

        if (value is double number)
        {
            return number.ToString("0", CultureInfo.InvariantCulture);
        }

        if (value is float numberFloat)
        {
            return numberFloat.ToString("0", CultureInfo.InvariantCulture);
        }

        if (value is decimal numberDecimal)
        {
            return numberDecimal.ToString("0", CultureInfo.InvariantCulture);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool IsHeaderRow(string? code, string? name)
    {
        var combined = $"{code} {name}".Trim();
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        var lower = combined.ToLowerInvariant();
        return lower.Contains("sku")
               || lower.Contains("gtin")
               || lower.Contains("штрих")
               || lower.Contains("наимен")
               || lower.Contains("name");
    }

    private static string? NormalizeImportedBarcode(string? value)
    {
        var trimmed = NormalizeIdentifier(value);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!IsDigitsOnly(trimmed))
        {
            return trimmed;
        }

        return trimmed.Length < 14 ? trimmed.PadLeft(14, '0') : trimmed;
    }

    private static void AddBarcodeVariants(HashSet<string> target, string? code)
    {
        var trimmed = NormalizeIdentifier(code);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        target.Add(trimmed);
        if (!IsDigitsOnly(trimmed))
        {
            return;
        }

        if (trimmed.Length == 13)
        {
            target.Add("0" + trimmed);
        }
        else if (trimmed.Length == 14 && trimmed.StartsWith("0", StringComparison.Ordinal))
        {
            target.Add(trimmed.Substring(1));
        }
    }

    private static bool IsBarcodeSeen(HashSet<string> seen, string? code)
    {
        var trimmed = NormalizeIdentifier(code);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (seen.Contains(trimmed))
        {
            return true;
        }

        if (!IsDigitsOnly(trimmed))
        {
            return false;
        }

        if (trimmed.Length == 13)
        {
            return seen.Contains("0" + trimmed);
        }

        if (trimmed.Length == 14 && trimmed.StartsWith("0", StringComparison.Ordinal))
        {
            return seen.Contains(trimmed.Substring(1));
        }

        return false;
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var itemsToDelete = GetSelectedItemsForDelete();
        if (itemsToDelete.Count == 0)
        {
            MessageBox.Show("Выберите товар.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmMessage = itemsToDelete.Count == 1
            ? "Удалить выбранный товар?"
            : $"Удалить выбранные товары ({itemsToDelete.Count})?";
        var confirm = MessageBox.Show(confirmMessage, "Товары", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var failed = new List<string>();
            foreach (var item in itemsToDelete)
            {
                try
                {
                    var deleted = await _services.WpfCatalogApi.TryDeleteItemAsync(item.Id).ConfigureAwait(true);
                    if (!deleted.IsSuccess)
                    {
                        throw new InvalidOperationException(deleted.Error ?? "Не удалось удалить товар через сервер.");
                    }
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.Name}: {ex.Message}");
                }
            }

            LoadItems();
            ClearItemForm();

            if (failed.Count > 0)
            {
                var message = "Не удалось удалить:\n" + string.Join("\n", failed);
                MessageBox.Show(message, "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IReadOnlyList<Item> GetSelectedItemsForDelete()
    {
        if (ItemsGrid.SelectedItems != null && ItemsGrid.SelectedItems.Count > 0)
        {
            return ItemsGrid.SelectedItems.Cast<Item>().ToList();
        }

        return _selectedItem != null ? new List<Item> { _selectedItem } : Array.Empty<Item>();
    }

    private void LocationsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedLocation = LocationsGrid.SelectedItem as Location;
        UpdateDeleteButtonsAvailability();
    }

    private void RestoreLocationSelection(long? locationId)
    {
        if (!locationId.HasValue)
        {
            return;
        }

        var location = _locations.FirstOrDefault(l => l.Id == locationId.Value);
        if (location == null)
        {
            return;
        }

        LocationsGrid.SelectedItem = location;
        LocationsGrid.ScrollIntoView(location);
    }

    private void LocationsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocationsGrid.SelectedItem is not Location)
        {
            return;
        }

        EditLocation_Click(sender, new RoutedEventArgs());
    }

    private async void DeleteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocation == null)
        {
            MessageBox.Show("Выберите место хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранное место хранения?", "Места хранения", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var deleted = await _services.WpfCatalogApi.TryDeleteLocationAsync(_selectedLocation.Id).ConfigureAwait(true);
            if (!deleted.IsSuccess)
            {
                throw new InvalidOperationException(deleted.Error ?? "Не удалось удалить место хранения через сервер.");
            }

            LoadLocations();
            ClearLocationForm();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PartnersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = PartnersGrid.SelectedItem as PartnerRow;
        _selectedPartner = row?.Partner;
        UpdateDeleteButtonsAvailability();
    }

    private void RestorePartnerSelection(long? partnerId)
    {
        if (!partnerId.HasValue)
        {
            return;
        }

        var row = _partners.FirstOrDefault(p => p.Partner.Id == partnerId.Value);
        if (row == null)
        {
            return;
        }

        PartnersGrid.SelectedItem = row;
        PartnersGrid.ScrollIntoView(row);
    }

    private void PartnersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PartnersGrid.SelectedItem is not PartnerRow)
        {
            return;
        }

        EditPartner_Click(sender, new RoutedEventArgs());
    }

    private void RestoreDocSelection(long? docId)
    {
        if (!docId.HasValue)
        {
            return;
        }

        var doc = _docs.FirstOrDefault(d => d.Id == docId.Value);
        if (doc == null)
        {
            return;
        }

        DocsGrid.SelectedItem = doc;
        DocsGrid.ScrollIntoView(doc);
    }

    private void RestoreOrderSelection(long? orderId)
    {
        if (!orderId.HasValue)
        {
            return;
        }

        var order = _orders.FirstOrDefault(o => o.Id == orderId.Value);
        if (order == null)
        {
            return;
        }

        OrdersGrid.SelectedItem = order;
        OrdersGrid.ScrollIntoView(order);
    }

    private async void DeletePartner_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartner == null)
        {
            MessageBox.Show("Выберите контрагента.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранного контрагента?", "Контрагенты", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var deleted = await _services.WpfPartnerApi.TryDeletePartnerAsync(_selectedPartner.Id).ConfigureAwait(true);
            if (!deleted.IsSuccess)
            {
                throw new InvalidOperationException(deleted.Error ?? "Не удалось удалить контрагента через сервер.");
            }

            LoadPartners();
            ClearPartnerForm();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewDocMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowNewDocDialog();
    }

    private void ShowNewDocDialog()
    {
        var window = new NewDocWindow(_services);
        window.Owner = this;
        if (window.ShowDialog() != true || !window.CreatedDocId.HasValue)
        {
            return;
        }

        LoadDocs();
        var created = _docs.FirstOrDefault(d => d.Id == window.CreatedDocId.Value)
                      ?? (_services.WpfReadApi.TryGetDoc(window.CreatedDocId.Value, out var apiDoc) ? apiDoc : null);
        if (created != null)
        {
            OpenDocDetails(created, window.CreatedDocUid);
        }
    }

    private void ImportMenu_Click(object sender, RoutedEventArgs e)
    {
        RunImportDialog();
    }

    private void RunImportDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSONL files (*.jsonl)|*.jsonl|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            RunImport(dialog.FileName);
        }
    }

    private void RunImport(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("Файл не найден.", "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var content = File.ReadAllText(path);
        var importCall = _services.WpfImportApi.TryImportJsonlAsync(content)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        if (!importCall.IsSuccess || importCall.Result == null)
        {
            MessageBox.Show(
                importCall.Error ?? "Не удалось выполнить импорт через сервер.",
                "Импорт",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var result = importCall.Result;
        var message = $"Импорт завершен.\nИмпортировано: {result.Imported}\nДубли: {result.Duplicates}\nОшибки: {result.Errors}";
        var icon = MessageBoxImage.Information;

        MessageBox.Show(message, "Импорт", MessageBoxButton.OK, icon);

        LoadDocs();
    }

    private void ViewStatus_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabStatusIndex);
    }

    private void ViewDocs_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabDocsIndex);
    }

    private void ViewOrders_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabOrdersIndex);
    }

    private void ViewItems_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabItemsIndex);
    }

    private void ViewLocations_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabLocationsIndex);
    }

    private void ViewPartners_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabPartnersIndex);
    }

    private void KmImport_Click(object sender, RoutedEventArgs e)
    {
        var window = new KmImportWindow(_services, () =>
        {
            LoadKmBatches();
        });
        window.Owner = this;
        window.ShowDialog();
    }

    private void KmOpenBatch_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedKmBatch();
    }

    private void KmEditBatch_Click(object sender, RoutedEventArgs e)
    {
        if (KmBatchesGrid.SelectedItem is not KmCodeBatch batch)
        {
            MessageBox.Show("Выберите пакет.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new KmBatchEditWindow(_services, batch, LoadKmBatches)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void KmDeleteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeleteModeEnabled("Маркировка"))
        {
            return;
        }

        if (KmBatchesGrid.SelectedItem is not KmCodeBatch batch)
        {
            MessageBox.Show("Выберите пакет.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить пакет \"{batch.FileName}\" и доступные коды в статусе \"В пуле\"?",
            "Маркировка",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Km.DeleteBatch(batch.Id);
            LoadKmBatches();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Маркировка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void KmBatchesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateDeleteButtonsAvailability();
    }

    private void KmBatchesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedKmBatch();
    }

    private void OpenSelectedKmBatch()
    {
        if (KmBatchesGrid.SelectedItem is not KmCodeBatch batch)
        {
            MessageBox.Show("Выберите пакет.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new KmBatchDetailsWindow(_services, batch, _adminDeleteModeEnabled, LoadKmBatches)
        {
            Owner = this
        };
        window.ShowDialog();
        LoadKmBatches();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dataDir = _services.BaseDir;
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir))
        {
            MessageBox.Show("Папка данных не найдена.", "Сервис", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = dataDir,
            UseShellExecute = true
        });
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var logsDir = _services.LogsDir;
        if (string.IsNullOrWhiteSpace(logsDir) || !Directory.Exists(logsDir))
        {
            MessageBox.Show("Папка логов не найдена.", "Сервис", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = logsDir,
            UseShellExecute = true
        });
    }

    private void OpenBackupManager_Click(object sender, RoutedEventArgs e)
    {
        var window = new BackupManagerWindow(_services);
        window.Owner = this;
        window.ShowDialog();
    }

    private void OpenHuRegistry_Click(object sender, RoutedEventArgs e)
    {
        var window = new HuRegistryWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenDbConnection_Click(object sender, RoutedEventArgs e)
    {
        var window = new DbConnectionWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenIncomingRequests_Click(object sender, RoutedEventArgs e)
    {
        var window = new IncomingRequestsWindow(_services, () =>
        {
            LoadStock(StatusSearchBox.Text);
            LoadOrders();
            UpdateItemRequestsBadge();
        })
        {
            Owner = this
        };
        window.ShowDialog();
        LoadStock(StatusSearchBox.Text);
        LoadOrders();
        UpdateItemRequestsBadge();
    }

    private void OpenTsdDevices_Click(object sender, RoutedEventArgs e)
    {
        var window = new TsdDeviceWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void LoadWarehouseBundles()
    {
        try
        {
            var filter = WarehouseBundleFilterCombo.SelectedItem as WarehouseBundleFilterOption;
            var result = await _services.WpfWarehouseTasks.TryListBundlesAsync(filter?.Status).ConfigureAwait(true);
            _warehouseBundles.Clear();
            if (!result.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    MessageBox.Show(result.ErrorMessage, "Задания", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            foreach (var row in result.Bundles)
            {
                _warehouseBundles.Add(row);
            }
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Load warehouse bundles failed", ex);
            MessageBox.Show(ex.Message, "Задания", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WarehouseBundlesRefresh_Click(object sender, RoutedEventArgs e) => LoadWarehouseBundles();

    private void WarehouseBundleFilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ExperimentalFeatureFlags.WarehouseTasksEnabled && MainTabs.SelectedIndex == TabTasksIndex)
        {
            LoadWarehouseBundles();
        }
    }

    private void WarehouseBundlesOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedWarehouseBundle();

    private void WarehouseBundlesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedWarehouseBundle();

    private void OpenSelectedWarehouseBundle()
    {
        if (WarehouseBundlesGrid.SelectedItem is not WarehouseBundleListRow row)
        {
            MessageBox.Show("Выберите пакет.", "Задания", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new WarehouseBundleDetailsWindow(_services, row.Id) { Owner = this };
        if (window.ShowDialog() == true)
        {
            LoadWarehouseBundles();
        }
    }

    private void WarehouseTestMove_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WarehouseTestBundleDialog(_services, WarehouseTestBundleMode.MoveHu) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            LoadWarehouseBundles();
        }
    }

    private void WarehouseTestAdopt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WarehouseTestBundleDialog(_services, WarehouseTestBundleMode.AdoptPalletPlan) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            LoadWarehouseBundles();
        }
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        var window = new AdminWindow(
            _services,
            () =>
            {
                LoadDocs();
                LoadOrders();
                LoadStock(StatusSearchBox.Text);
                LoadKmBatches();
                UpdateItemRequestsBadge();
            });
        window.Owner = this;
        window.ShowDialog();
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= MainTabs.Items.Count)
        {
            return;
        }

        MainTabs.SelectedIndex = index;
    }

    private void UomMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new UomWindow(_services, () => LoadUoms());
        window.Owner = this;
        window.ShowDialog();
        LoadUoms();
    }

    private void WriteOffReasonsMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new WriteOffReasonWindow(_services, null)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ItemTypesMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new ItemTypeWindow(_services, () =>
        {
            LoadItemTypes();
            LoadItems(ItemsSearchBox?.Text);
            LoadStock(StatusSearchBox.Text);
            LoadLowStockView();
        })
        {
            Owner = this
        };
        window.ShowDialog();
        LoadItemTypes();
        LoadItems(ItemsSearchBox?.Text);
        LoadStock(StatusSearchBox.Text);
        LoadLowStockView();
    }

    private void TaraMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new TaraWindow(_services, LoadTaras)
        {
            Owner = this
        };
        window.ShowDialog();
        LoadTaras();
    }

    private void PackagingManager_Click(object sender, RoutedEventArgs e)
    {
        var window = new PackagingManagerWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();

        LoadItems();
        LoadStock(StatusSearchBox.Text);
    }

    private void DocNumberingSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new DocNumberingSettingsWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenMarking_Click(object sender, RoutedEventArgs e)
    {
        var window = new MarkingWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
        LoadOrders();
    }

    private void ImportErrors_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabDocsIndex);
        var window = new ImportErrorsWindow(_services, () =>
        {
            LoadDocs();
            LoadStock(StatusSearchBox.Text);
        });
        window.Owner = this;
        window.ShowDialog();
    }
    private void UpdateItemRequestsBadge()
    {
        if (ItemRequestsBadge == null || ItemRequestsCountText == null)
        {
            return;
        }

        try
        {
            var summary = _services.WpfIncomingRequestsApi.TryGetSummary(out var apiSummary)
                ? apiSummary
                : new IncomingRequestsSummary(0, 0);

            var itemCount = summary.ItemRequestsPending;
            var orderCount = summary.OrderRequestsPending;
            var count = itemCount + orderCount;
            ItemRequestsCountText.Text = count.ToString();
            ItemRequestsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ItemRequestsButton.ToolTip = count > 0
                ? $"Входящие запросы: {count} (товары: {itemCount}, заказы: {orderCount})"
                : "Входящие запросы";
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Incoming requests badge update failed", ex);
        }
    }

    private void ClearItemForm()
    {
        _selectedItem = null;
        ItemsGrid.SelectedItem = null;
        UpdateDeleteButtonsAvailability();
    }

    private void ClearLocationForm()
    {
        _selectedLocation = null;
        LocationsGrid.SelectedItem = null;
        UpdateDeleteButtonsAvailability();
    }

    private void ClearPartnerForm()
    {
        _selectedPartner = null;
        PartnersGrid.SelectedItem = null;
        UpdateDeleteButtonsAvailability();
    }

    private static string GetPartnerStatusLabel(PartnerStatus status)
    {
        return status switch
        {
            PartnerStatus.Supplier => "Поставщик",
            PartnerStatus.Client => "Клиент",
            PartnerStatus.Both => "Клиент и поставщик",
            _ => "Неизвестно"
        };
    }


    private static string? NormalizeIdentifier(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsDigitsOnly(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPostgresConstraint(PostgresException ex)
    {
        return string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal);
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static string FormatQtyWithUom(double value, string? baseUom)
    {
        var formattedValue = FormatQty(value);
        if (string.IsNullOrWhiteSpace(baseUom))
        {
            return formattedValue;
        }

        return $"{formattedValue} {baseUom.Trim()}";
    }

    private static string FormatOptionalQtyWithUom(double value, string? baseUom)
        => value > 0.000001d ? FormatQtyWithUom(value, baseUom) : string.Empty;

    private static string CombineNonEmptyLines(params string[] lines)
    {
        var nonEmptyLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        return nonEmptyLines.Length == 0 ? "—" : string.Join(Environment.NewLine, nonEmptyLines);
    }

    private static string TranslatePalletStatus(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "PLANNED" => "Ожидает",
            "PRINTED" => "Этикетка напечатана",
            "FILLED" => "Наполнена",
            "CANCELLED" => "Отменена",
            _ => string.IsNullOrWhiteSpace(status) ? "—" : status.Trim()
        };
    }

    private sealed record DocTypeFilterOption(DocType? Type, string Name);

    private sealed record DocStatusFilterOption(DocStatus? Status, string Name);

    private sealed record PartnerRow(Partner Partner, string StatusDisplay)
    {
        public long Id => Partner.Id;
        public string Name => Partner.Name;
        public string? Code => Partner.Code;
        public DateTime CreatedAt => Partner.CreatedAt;
    }

    private sealed record ProductionNeedDisplayRow
    {
        public long ItemId { get; init; }
        public string Gtin { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public double FreeStockQty { get; init; }
        public double MinStockQty { get; init; }
        public double ToCloseOrdersQty { get; init; }
        public double ToMinStockQty { get; init; }
        public double OpenInternalOrderQty { get; init; }
        public string OpenInternalOrderRefs { get; init; } = string.Empty;
        public double PlannedPalletQty { get; init; }
        public double FilledPalletQty { get; init; }
        public int PlannedPalletCount { get; init; }
        public int FilledPalletCount { get; init; }
        public double RemainingPalletQty { get; init; }
        public double QtyToCreate { get; init; }
        public bool CanCreateOrder { get; init; }
        public string Reason { get; init; } = string.Empty;
        public double TotalToMakeQty { get; init; }
        public string StockDisplay => $"{FormatQty(FreeStockQty)} / {FormatQty(MinStockQty)}";
        public string FilledPalletDisplay => PlannedPalletCount > 0
            ? $"{FilledPalletCount} / {PlannedPalletCount} паллет, {FormatQty(FilledPalletQty)} шт"
            : FormatQty(FilledPalletQty);
    }

    private interface IExpandableStockRow : INotifyPropertyChanged
    {
        long ItemId { get; }
        bool IsExpanded { get; set; }
        string ExpandMarker { get; set; }
    }

    private sealed class StockDisplayRow : IExpandableStockRow
    {
        private bool _isExpanded;
        private string _expandMarker = "▶";

        public event PropertyChangedEventHandler? PropertyChanged;

        public long ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public string PackagingDisplay { get; init; } = string.Empty;
        public string BaseDisplay { get; init; } = string.Empty;
        public bool IsBelowMin { get; init; }
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public string ExpandMarker
        {
            get => _expandMarker;
            set
            {
                if (string.Equals(_expandMarker, value, StringComparison.Ordinal))
                {
                    return;
                }

                _expandMarker = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandMarker)));
            }
        }

        public IReadOnlyList<StockDetailDisplayRow> Details { get; init; } = Array.Empty<StockDetailDisplayRow>();
    }

    private sealed class StockDetailDisplayRow
    {
        public string LocationCode { get; init; } = string.Empty;
        public string HuDisplay { get; init; } = string.Empty;
        public string BaseDisplay { get; init; } = string.Empty;
        public string OriginOrderDisplay { get; init; } = "—";
        public string ReservedOrderDisplay { get; init; } = "не зарезервировано";
        public string ReservedCustomerDisplay { get; init; } = "не зарезервировано";
    }

    private sealed class WarehouseProductionStateDisplayRow : IExpandableStockRow, INotifyPropertyChanged
    {
        private bool _isExpanded;
        private string _expandMarker = "▶";

        public event PropertyChangedEventHandler? PropertyChanged;

        public long ItemId { get; init; }
        public string ItemName { get; private set; } = string.Empty;
        public string? Barcode { get; private set; }
        public string? Gtin { get; private set; }
        public string ItemTypeName { get; private set; } = string.Empty;
        public string? Brand { get; private set; }
        public string BaseUom { get; private set; } = "шт";
        public double StockQty { get; private set; }
        public double FreeQty { get; private set; }
        public double ReservedQty { get; private set; }
        public double MinStockQty { get; private set; }
        public double BelowMinQty { get; private set; }
        public double CustomerOpenDemandQty { get; private set; }
        public double PrdPlannedQty { get; private set; }
        public double PrdFilledQty { get; private set; }
        public double InternalRemainingQty { get; private set; }
        public double RemainingNeedQty { get; private set; }
        public string NeedReason { get; private set; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<WarehouseProductionStateHuDisplayRow> WarehouseHuRows { get; private set; } = Array.Empty<WarehouseProductionStateHuDisplayRow>();
        public IReadOnlyList<WarehouseProductionStateCustomerOrderDisplayRow> CustomerOrders { get; private set; } = Array.Empty<WarehouseProductionStateCustomerOrderDisplayRow>();
        public IReadOnlyList<WarehouseProductionStateInternalOrderDisplayRow> InternalOrders { get; private set; } = Array.Empty<WarehouseProductionStateInternalOrderDisplayRow>();
        public IReadOnlyList<WarehouseProductionStatePalletDisplayRow> ProductionReceipts { get; private set; } = Array.Empty<WarehouseProductionStatePalletDisplayRow>();
        public IReadOnlyList<WarehouseProductionStateNeedBreakdownDisplayRow> NeedBreakdownRows { get; private set; } = Array.Empty<WarehouseProductionStateNeedBreakdownDisplayRow>();
        public bool IsBelowMin => BelowMinQty > 0.000001d;

        public void ApplyFrom(WarehouseProductionStateRow row)
        {
            ItemName = row.ItemName;
            Barcode = row.Barcode;
            Gtin = row.Gtin;
            ItemTypeName = string.IsNullOrWhiteSpace(row.ItemType) ? "Без типа" : row.ItemType;
            Brand = row.Brand;
            BaseUom = string.IsNullOrWhiteSpace(row.BaseUom) ? "шт" : row.BaseUom;
            StockQty = row.StockQty;
            FreeQty = row.FreeQty;
            ReservedQty = row.ReservedQty;
            MinStockQty = row.MinStockQty;
            BelowMinQty = row.BelowMinQty;
            CustomerOpenDemandQty = row.CustomerOpenDemandQty;
            PrdPlannedQty = row.PrdPlannedQty;
            PrdFilledQty = row.PrdFilledQty;
            InternalRemainingQty = row.InternalRemainingQty;
            RemainingNeedQty = row.RemainingNeedQty;
            NeedReason = row.NeedReason;
            Warnings = row.Warnings;
            WarehouseHuRows = row.HuRows.Select(hu => new WarehouseProductionStateHuDisplayRow
            {
                Location = hu.Location,
                HuCode = string.IsNullOrWhiteSpace(hu.HuCode) ? "Без HU" : hu.HuCode,
                QtyDisplay = FormatQtyWithUom(hu.Qty, row.BaseUom),
                ReservedOrderDisplay = string.IsNullOrWhiteSpace(hu.ReservedCustomerOrderRef) ? "не зарезервировано" : hu.ReservedCustomerOrderRef!,
                ReservedCustomerDisplay = string.IsNullOrWhiteSpace(hu.ReservedCustomerName) ? "не зарезервировано" : hu.ReservedCustomerName!,
                StockStatus = hu.StockStatus
            }).ToList();
            CustomerOrders = row.CustomerOrders.Select(order => new WarehouseProductionStateCustomerOrderDisplayRow
            {
                OrderRef = order.OrderRef,
                PartnerName = string.IsNullOrWhiteSpace(order.PartnerName) ? "—" : order.PartnerName!,
                Status = order.Status,
                QtyOrderedDisplay = FormatQtyWithUom(order.QtyOrdered, row.BaseUom),
                ShippedQtyDisplay = FormatQtyWithUom(order.ShippedQty, row.BaseUom),
                RemainingQtyDisplay = FormatQtyWithUom(order.RemainingQty, row.BaseUom)
            }).ToList();
            InternalOrders = row.InternalOrders.Select(order => new WarehouseProductionStateInternalOrderDisplayRow
            {
                OrderRef = order.OrderRef,
                Status = order.Status,
                QtyOrderedDisplay = FormatQtyWithUom(order.QtyOrdered, row.BaseUom),
                ProducedQtyDisplay = FormatQtyWithUom(order.ProducedQty, row.BaseUom),
                RemainingQtyDisplay = FormatQtyWithUom(order.RemainingQty, row.BaseUom)
            }).ToList();
            ProductionReceipts = row.ProductionReceipts.Select(prd => new WarehouseProductionStatePalletDisplayRow
            {
                PrdRef = prd.PrdRef,
                HuCode = prd.HuCode,
                PalletStatus = string.IsNullOrWhiteSpace(prd.PalletStatusDisplay)
                    ? TranslatePalletStatus(prd.PalletStatus)
                    : prd.PalletStatusDisplay,
                QtyDisplay = FormatQtyWithUom(prd.Qty > 0 ? prd.Qty : prd.PlannedQty, row.BaseUom),
                SourceOrderRef = string.IsNullOrWhiteSpace(prd.SourceOrderRef) ? "—" : prd.SourceOrderRef,
                StatusNote = prd.StatusNote,
                PlannedQtyDisplay = FormatQtyWithUom(prd.PlannedQty, row.BaseUom),
                FilledQtyDisplay = FormatQtyWithUom(prd.FilledQty, row.BaseUom),
                StockEffect = prd.StockEffect,
                Composition = prd.Composition
            }).ToList();
            NeedBreakdownRows =
            [
                new WarehouseProductionStateNeedBreakdownDisplayRow
                {
                    DemandToCloseDisplay = FormatQtyWithUom(row.NeedBreakdown.DemandToCloseCustomerOrders, row.BaseUom),
                    DemandToMinDisplay = FormatQtyWithUom(row.NeedBreakdown.DemandToMinStock, row.BaseUom),
                    AlreadyPlannedInternalDisplay = FormatQtyWithUom(row.NeedBreakdown.AlreadyPlannedInternal, row.BaseUom),
                    AlreadyPlannedPrdDisplay = FormatQtyWithUom(row.NeedBreakdown.AlreadyPlannedPrd, row.BaseUom),
                    FilledDisplay = FormatQtyWithUom(row.PrdFilledQty, row.BaseUom),
                    RemainingToCreateDisplay = FormatQtyWithUom(row.NeedBreakdown.RemainingToCreate, row.BaseUom),
                    NeedReason = row.NeedReason
                }
            ];
            NotifyDisplayPropertiesChanged();
        }

        private void NotifyDisplayPropertiesChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public string ExpandMarker
        {
            get => _expandMarker;
            set
            {
                if (string.Equals(_expandMarker, value, StringComparison.Ordinal))
                {
                    return;
                }

                _expandMarker = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandMarker)));
            }
        }

        public string StockQtyDisplay => FormatQtyWithUom(StockQty, BaseUom);
        public string FreeQtyDisplay => FormatQtyWithUom(FreeQty, BaseUom);
        public string ReservedQtyDisplay => FormatQtyWithUom(ReservedQty, BaseUom);
        public string MinStockQtyDisplay => FormatQtyWithUom(MinStockQty, BaseUom);
        public string BelowMinQtyDisplay => FormatQtyWithUom(BelowMinQty, BaseUom);
        public string CustomerOpenDemandQtyDisplay => FormatQtyWithUom(CustomerOpenDemandQty, BaseUom);
        public string PrdPlannedQtyDisplay => FormatQtyWithUom(PrdPlannedQty, BaseUom);
        public string PrdFilledQtyDisplay => FormatQtyWithUom(PrdFilledQty, BaseUom);
        public string RemainingNeedQtyDisplay => FormatQtyWithUom(RemainingNeedQty, BaseUom);
        public string ProductSubline
        {
            get
            {
                var sku = !string.IsNullOrWhiteSpace(Barcode)
                    ? $"ШК: {Barcode}"
                    : !string.IsNullOrWhiteSpace(Gtin)
                        ? $"GTIN: {Gtin}"
                        : string.Empty;
                return string.IsNullOrWhiteSpace(sku) ? ItemTypeName : $"{sku} · {ItemTypeName}";
            }
        }
        public string MinStockSummary => MinStockQty > 0.000001d
            ? FormatQtyWithUom(MinStockQty, BaseUom)
            : "—";
        public string NeedSummary => CombineNonEmptyLines(
            CustomerOpenDemandQty > 0.000001d
                ? $"Всего в заказах для клиентов: {FormatQtyWithUom(CustomerOpenDemandQty, BaseUom)}"
                : string.Empty,
            BelowMinQty > 0.000001d
                ? $"До минимума: {FormatQtyWithUom(BelowMinQty, BaseUom)}"
                : string.Empty);
        public string PlanSummary => CombineNonEmptyLines(
            InternalRemainingQty > 0.000001d
                ? $"Во внутренних заказах: {FormatQtyWithUom(InternalRemainingQty, BaseUom)}"
                : string.Empty,
            PrdPlannedQty > 0.000001d
                ? $"В PRD/плане: {FormatQtyWithUom(PrdPlannedQty, BaseUom)}"
                : string.Empty);
        public string FilledSummary => PrdFilledQty > 0.000001d
            ? FormatQtyWithUom(PrdFilledQty, BaseUom)
            : "—";
        public string RemainingNeedSummary
        {
            get
            {
                if (RemainingNeedQty > 0.000001d)
                {
                    return $"Произвести: {FormatQtyWithUom(RemainingNeedQty, BaseUom)}";
                }

                var hasNeedOrPlan = CustomerOpenDemandQty > 0.000001d
                                    || BelowMinQty > 0.000001d
                                    || InternalRemainingQty > 0.000001d
                                    || PrdPlannedQty > 0.000001d
                                    || PrdFilledQty > 0.000001d;
                return hasNeedOrPlan ? "Покрыто" : "—";
            }
        }
        public System.Windows.Media.Brush RemainingNeedBrush => RemainingNeedQty > 0.000001d
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.ForestGreen;
        public FontWeight RemainingNeedFontWeight => RemainingNeedQty > 0.000001d
            ? FontWeights.SemiBold
            : FontWeights.Normal;
    }

    private sealed record WarehouseProductionStateHuDisplayRow
    {
        public string Location { get; init; } = string.Empty;
        public string HuCode { get; init; } = string.Empty;
        public string QtyDisplay { get; init; } = string.Empty;
        public string ReservedOrderDisplay { get; init; } = string.Empty;
        public string ReservedCustomerDisplay { get; init; } = string.Empty;
        public string StockStatus { get; init; } = string.Empty;
    }

    private sealed record WarehouseProductionStateCustomerOrderDisplayRow
    {
        public string OrderRef { get; init; } = string.Empty;
        public string PartnerName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string QtyOrderedDisplay { get; init; } = string.Empty;
        public string ShippedQtyDisplay { get; init; } = string.Empty;
        public string RemainingQtyDisplay { get; init; } = string.Empty;
    }

    private sealed record WarehouseProductionStateInternalOrderDisplayRow
    {
        public string OrderRef { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string QtyOrderedDisplay { get; init; } = string.Empty;
        public string ProducedQtyDisplay { get; init; } = string.Empty;
        public string RemainingQtyDisplay { get; init; } = string.Empty;
    }

    private sealed record WarehouseProductionStatePalletDisplayRow
    {
        public string PrdRef { get; init; } = string.Empty;
        public string HuCode { get; init; } = string.Empty;
        public string PalletStatus { get; init; } = string.Empty;
        public string QtyDisplay { get; init; } = string.Empty;
        public string SourceOrderRef { get; init; } = string.Empty;
        public string StatusNote { get; init; } = string.Empty;
        public string PlannedQtyDisplay { get; init; } = string.Empty;
        public string FilledQtyDisplay { get; init; } = string.Empty;
        public string StockEffect { get; init; } = string.Empty;
        public string Composition { get; init; } = string.Empty;
    }

    private sealed record WarehouseProductionStateNeedBreakdownDisplayRow
    {
        public string DemandToCloseDisplay { get; init; } = string.Empty;
        public string DemandToMinDisplay { get; init; } = string.Empty;
        public string AlreadyPlannedInternalDisplay { get; init; } = string.Empty;
        public string AlreadyPlannedPrdDisplay { get; init; } = string.Empty;
        public string FilledDisplay { get; init; } = string.Empty;
        public string RemainingToCreateDisplay { get; init; } = string.Empty;
        public string NeedReason { get; init; } = string.Empty;
    }

    private sealed record WarehouseBundleFilterOption(string? Status, string Label);

    private sealed record StockLocationFilterOption(string? Code, string Name);

    private sealed record StockHuFilterOption(string? Code, string Name);

    private sealed record StockItemTypeFilterOption(long? Id, string Name);

    private sealed record LowStockSnapshot(
        long ItemId,
        string ItemName,
        string ItemTypeName,
        string BaseUom,
        double Qty,
        double? MinStockQty,
        bool IsBelowMin);

    private sealed record LowStockDisplayRow
    {
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public string QtyDisplay { get; init; } = string.Empty;
        public string MinStockQtyDisplay { get; init; } = string.Empty;
        public string ShortageDisplay { get; init; } = string.Empty;
    }

    private sealed record ImportItemsSummary(int Created, int Duplicates, int EmptyRows, int InvalidRows, int Errors);
}

