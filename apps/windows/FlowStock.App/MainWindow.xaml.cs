using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly ObservableCollection<CatalogItemFilterOption> _itemBrandFilters = new();
    private readonly ObservableCollection<CatalogItemFilterOption> _itemVolumeFilters = new();
    private readonly ObservableCollection<CatalogItemFilterOption> _itemUomFilters = new();
    private readonly ICollectionView _itemsView;
    private readonly ObservableCollection<WarehouseProductionStateDisplayRow> _warehouseProductionStateRows = new();
    private readonly ObservableCollection<LowStockDisplayRow> _lowStock = new();
    private readonly ObservableCollection<ProductionNeedDisplayRow> _productionNeedRows = new();
    private readonly ObservableCollection<StockLocationFilterOption> _stockLocationFilters = new();
    private readonly ObservableCollection<StockHuFilterOption> _stockHuFilters = new();
    private readonly ObservableCollection<StockItemTypeFilterOption> _stockItemTypeFilters = new();
    private readonly ObservableCollection<KmCodeBatch> _kmBatches = new();
    private readonly IDisposable _liveRefreshSubscription;
    private readonly HashSet<int> _pendingLiveRefreshTabs = new();
    private readonly HashSet<long> _expandedStockItemIds = new();
    private bool _autoRefreshInProgress;
    private bool _serverApiUnavailableAtStartup;
    private bool _suppressStockFilterSelectionChanged;
    private bool _suppressItemFilterSelectionChanged;
    private static readonly TimeSpan StockRefreshDebounceInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StockGridScrollIdleDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan StockRefreshDeferWhileScrolling = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ItemRequestsBadgeRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommercialStatisticsRefreshDebounceInterval =
        TimeSpan.FromMilliseconds(300);
    private DispatcherTimer? _stockRefreshDebounceTimer;
    private bool _stockRefreshDebounceTickAttached;
    private DispatcherTimer? _stockGridScrollIdleTimer;
    private DispatcherTimer? _deferredStockRefreshTimer;
    private bool _deferredStockRefreshTickAttached;
    private string? _pendingStockSearch;
    private string? _warehouseProductionStateFingerprint;
    private bool _warehouseProductionStateLoadInProgress;
    private bool _stockGridScrollTrackingAttached;
    private System.Windows.Controls.ScrollViewer? _warehouseProductionStateScrollViewer;
    private bool _stockGridUserScrolling;
    private DispatcherTimer? _itemRequestsBadgeRefreshTimer;
    private bool _itemRequestsBadgeUpdateInProgress;
    private bool _itemRequestsBadgeUpdatePending;
    private readonly List<DocTypeFilterOption> _docTypeFilters = new()
    {
        new DocTypeFilterOption(null, "Все"),
        new DocTypeFilterOption(DocType.Inbound, "Приемка"),
        new DocTypeFilterOption(DocType.ProductionReceipt, "Выпуск продукции"),
        new DocTypeFilterOption(DocType.Outbound, "Отгрузка"),
        new DocTypeFilterOption(DocType.Move, "Перемещение"),
        new DocTypeFilterOption(DocType.WriteOff, "Списание"),
        new DocTypeFilterOption(DocType.Inventory, "Инвентаризация"),
        new DocTypeFilterOption(DocType.InventoryCorrection, "Корректировка остатков")
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
    private const int TabStatisticsIndex = 4;
    private const int TabTasksIndex = 5;
    private const int TabItemsIndex = 6;
    private const int TabLocationsIndex = 7;
    private const int TabPartnersIndex = 8;
    private const int TabKmIndex = 9;
    private const int OrdersPageSize = 15;
    private readonly CommercialStatisticsViewState _commercialStatisticsState = new(pageSize: 100);
    private readonly CommercialStatisticsAutoRefreshCoordinator _commercialStatisticsAutoRefresh = new();
    private readonly ObservableCollection<CommercialStatisticsStatusFilterOption> _statisticsStatusOptions =
        new(CommercialStatisticsFilterOptions.BuildStatuses());
    private IReadOnlyList<CommercialStatisticsEntityFilterOption> _statisticsPartnerOptions = [];
    private IReadOnlyList<CommercialStatisticsEntityFilterOption> _statisticsItemOptions = [];
    private IReadOnlyList<CommercialStatisticsTextFilterOption> _statisticsGtinOptions = [];
    private IReadOnlyList<CommercialStatisticsTextFilterOption> _statisticsBrandOptions = [];
    private IReadOnlyList<CommercialStatisticsTextFilterOption> _statisticsVolumeOptions = [];
    private long? _statisticsPartnerId;
    private long? _statisticsItemId;
    private string? _statisticsGtin;
    private string? _statisticsBrand;
    private string? _statisticsVolume;
    private DispatcherTimer? _commercialStatisticsRefreshTimer;
    private bool _commercialStatisticsInitialLoadStarted;
    private bool _commercialStatisticsSearchHandlersAttached;
    private bool _suppressCommercialStatisticsFilterEvents;
    private int _ordersPagedDepth;
    private bool _ordersHasMore;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        _itemsView.Filter = FilterCatalogItem;
        ItemBrandFilterList.ItemsSource = _itemBrandFilters;
        ItemVolumeFilterList.ItemsSource = _itemVolumeFilters;
        ItemUomFilterList.ItemsSource = _itemUomFilters;
        LocationsGrid.ItemsSource = _locations;
        PartnersGrid.ItemsSource = _partners;
        DocsGrid.ItemsSource = _docs;
        OrdersGrid.ItemsSource = _orders;
        WarehouseBundlesGrid.ItemsSource = _warehouseBundles;
        InitializeCommercialStatisticsFilters();
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
        foreach (var grid in new[]
                 {
                     StockGrid, WarehouseProductionStateGrid, ProductionNeedGrid, DocsGrid, OrdersGrid,
                     WarehouseBundlesGrid, ItemsGrid, LocationsGrid, PartnersGrid, KmBatchesGrid
                 })
        {
            grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshPendingActiveTab);
        }

        TryLoadAllOnStartup();
        ClearItemForm();
        ClearLocationForm();
        ClearPartnerForm();

        _liveRefreshSubscription = _services.LiveRefresh.Register(
            CanApplyLiveRefresh,
            ApplyLiveRefresh,
            MarkAllTabsPendingLiveRefresh);
        Loaded += MainWindow_Loaded;
        Activated += (_, _) => RefreshPendingActiveTab();
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

        if (OrdersCreateControlButton != null)
        {
            var selectedOrders = OrdersGrid.SelectedItems
                .OfType<Order>()
                .ToArray();
            OrdersCreateControlButton.IsEnabled = selectedOrders.Length > 0
                                                  && selectedOrders.All(order => order.Type == OrderType.Customer && order.Status == OrderStatus.Accepted);
            OrdersCreateControlButton.ToolTip = OrdersCreateControlButton.IsEnabled
                ? null
                : "Контроль можно создать только для выбранных клиентских заказов в статусе Готов.";
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
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        AttachWarehouseProductionStateGridScrollTracking();
        ScheduleItemRequestsBadgeUpdate();
        StartItemRequestsBadgeRefreshTimer();
        RefreshHuCorrectionAvailability();

        if (_serverApiUnavailableAtStartup)
        {
            return;
        }

        RefreshPendingActiveTab();
    }

    private void AttachWarehouseProductionStateGridScrollTracking()
    {
        if (_stockGridScrollTrackingAttached)
        {
            return;
        }

        _warehouseProductionStateScrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(WarehouseProductionStateGrid);
        if (_warehouseProductionStateScrollViewer == null)
        {
            return;
        }

        _warehouseProductionStateScrollViewer.ScrollChanged += OnWarehouseProductionStateGridScrollChanged;
        _stockGridScrollTrackingAttached = true;
    }

    private void OnWarehouseProductionStateGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.01d && Math.Abs(e.ViewportHeightChange) < 0.01d)
        {
            return;
        }

        _stockGridUserScrolling = true;
        _stockGridScrollIdleTimer ??= new DispatcherTimer { Interval = StockGridScrollIdleDelay };
        _stockGridScrollIdleTimer.Tick -= OnStockGridScrollIdleTimerTick;
        _stockGridScrollIdleTimer.Tick += OnStockGridScrollIdleTimerTick;
        _stockGridScrollIdleTimer.Stop();
        _stockGridScrollIdleTimer.Start();
    }

    private void OnStockGridScrollIdleTimerTick(object? sender, EventArgs e)
    {
        _stockGridScrollIdleTimer?.Stop();
        _stockGridUserScrolling = false;

        if (!string.IsNullOrEmpty(_pendingStockSearch) || _deferredStockRefreshTimer?.IsEnabled == true)
        {
            ScheduleDeferredStockRefresh(_pendingStockSearch ?? StatusSearchBox.Text);
        }
    }

    private bool ShouldDeferStockRefresh()
    {
        return _stockGridUserScrolling || _warehouseProductionStateLoadInProgress;
    }

    private void ScheduleDeferredStockRefresh(string? search)
    {
        _pendingStockSearch = search;
        _deferredStockRefreshTimer ??= new DispatcherTimer { Interval = StockRefreshDeferWhileScrolling };
        if (!_deferredStockRefreshTickAttached)
        {
            _deferredStockRefreshTimer.Tick += (_, _) =>
            {
                _deferredStockRefreshTimer!.Stop();
                if (ShouldDeferStockRefresh())
                {
                    ScheduleDeferredStockRefresh(_pendingStockSearch);
                    return;
                }

                LoadStock(_pendingStockSearch);
            };
            _deferredStockRefreshTickAttached = true;
        }

        _deferredStockRefreshTimer.Stop();
        _deferredStockRefreshTimer.Start();
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
        _itemRequestsBadgeRefreshTimer?.Stop();
        _commercialStatisticsRefreshTimer?.Stop();
        _liveRefreshSubscription.Dispose();
    }

    private bool CanApplyLiveRefresh()
    {
        return IsLoaded
               && IsVisible
               && IsActive
               && !_autoRefreshInProgress
               && !IsActiveTabEditing();
    }

    private bool IsActiveTabEditing()
    {
        if (MainTabs.SelectedIndex is TabItemsIndex or TabLocationsIndex or TabPartnersIndex
            && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox)
        {
            return true;
        }

        return MainTabs.SelectedIndex switch
        {
            TabStatusIndex => WpfLiveRefreshGuard.IsDataGridEditing(StockGrid)
                              || WpfLiveRefreshGuard.IsDataGridEditing(WarehouseProductionStateGrid),
            TabProductionNeedIndex => WpfLiveRefreshGuard.IsDataGridEditing(ProductionNeedGrid),
            TabDocsIndex => WpfLiveRefreshGuard.IsDataGridEditing(DocsGrid),
            TabOrdersIndex => WpfLiveRefreshGuard.IsDataGridEditing(OrdersGrid),
            TabStatisticsIndex => false,
            TabTasksIndex => WpfLiveRefreshGuard.IsDataGridEditing(WarehouseBundlesGrid),
            TabItemsIndex => WpfLiveRefreshGuard.IsDataGridEditing(ItemsGrid),
            TabLocationsIndex => WpfLiveRefreshGuard.IsDataGridEditing(LocationsGrid),
            TabPartnersIndex => WpfLiveRefreshGuard.IsDataGridEditing(PartnersGrid),
            TabKmIndex => WpfLiveRefreshGuard.IsDataGridEditing(KmBatchesGrid),
            _ => false
        };
    }

    private void ApplyLiveRefresh()
    {
        MarkAllTabsPendingLiveRefresh();
        RefreshPendingActiveTab();
    }

    private void MarkAllTabsPendingLiveRefresh()
    {
        for (var tabIndex = TabStatusIndex; tabIndex <= TabKmIndex; tabIndex++)
        {
            _pendingLiveRefreshTabs.Add(tabIndex);
        }
    }

    private void RefreshPendingActiveTab()
    {
        var selectedIndex = MainTabs.SelectedIndex;
        if (!_pendingLiveRefreshTabs.Contains(selectedIndex) || !CanApplyLiveRefresh())
        {
            return;
        }

        _pendingLiveRefreshTabs.Remove(selectedIndex);
        RefreshActiveTab();
    }

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, MainTabs))
        {
            return;
        }

        RefreshPendingActiveTab();
        if (MainTabs.SelectedIndex == TabStatisticsIndex
            && !_commercialStatisticsInitialLoadStarted
            && !_serverApiUnavailableAtStartup)
        {
            _ = LoadCommercialStatisticsImmediatelyAsync();
        }
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
                    if (ShouldDeferStockRefresh())
                    {
                        ScheduleDeferredStockRefresh(StatusSearchBox.Text);
                        break;
                    }

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
                case TabStatisticsIndex:
                    _ = LoadCommercialStatisticsImmediatelyAsync();
                    break;
                case TabTasksIndex:
                    if (ExperimentalFeatureFlags.WarehouseTasksEnabled)
                    {
                        LoadWarehouseBundles();
                    }
                    break;
                case TabItemsIndex:
                    LoadItems();
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
        if (DeleteKeyGesture.IsDeleteGesture(e))
        {
            if (TryHandleMainGridDeleteGesture())
            {
                e.Handled = true;
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
        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        foreach (var item in items)
        {
            _items.Add(item);
        }
        if (ItemsSearchBox != null && search != null)
        {
            ItemsSearchBox.Text = search;
        }
        RebuildItemFilters();
        RebuildCommercialStatisticsCatalogFilters();
        ApplyItemFilters();
        RestoreItemSelection(selectedId);
    }

    private bool FilterCatalogItem(object? obj)
    {
        if (obj is not Item item)
        {
            return false;
        }

        return CatalogItemFilter.MatchesGroup(item.Brand, _itemBrandFilters)
               && CatalogItemFilter.MatchesGroup(item.Volume, _itemVolumeFilters)
               && CatalogItemFilter.MatchesGroup(item.BaseUom, _itemUomFilters)
               && CatalogItemFilter.MatchesSearch(item, ItemsSearchBox?.Text);
    }

    private void RebuildItemFilters()
    {
        _suppressItemFilterSelectionChanged = true;
        try
        {
            CatalogItemFilter.RebuildOptions(_itemBrandFilters, _items.Select(item => item.Brand), ItemFilterOptionChanged);
            CatalogItemFilter.RebuildOptions(_itemVolumeFilters, _items.Select(item => item.Volume), ItemFilterOptionChanged);
            CatalogItemFilter.RebuildOptions(_itemUomFilters, _items.Select(item => item.BaseUom), ItemFilterOptionChanged);
        }
        finally
        {
            _suppressItemFilterSelectionChanged = false;
        }
    }

    private void ItemFilterOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressItemFilterSelectionChanged && e.PropertyName == nameof(CatalogItemFilterOption.IsChecked))
        {
            ApplyItemFilters();
        }
    }

    private void ApplyItemFilters()
    {
        var selectedId = _selectedItem?.Id;
        _itemsView.Refresh();
        RestoreItemSelection(selectedId);
    }

    private void SetItemFilters(ObservableCollection<CatalogItemFilterOption> options, bool isChecked)
    {
        _suppressItemFilterSelectionChanged = true;
        try
        {
            CatalogItemFilter.SetAll(options, isChecked);
        }
        finally
        {
            _suppressItemFilterSelectionChanged = false;
        }

        ApplyItemFilters();
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
        RebuildCommercialStatisticsCatalogFilters();
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
        _pendingStockSearch = search;

        if (ShouldDeferStockRefresh())
        {
            ScheduleDeferredStockRefresh(search);
            return;
        }

        if (debounce)
        {
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
        if (_warehouseProductionStateLoadInProgress)
        {
            ScheduleDeferredStockRefresh(search);
            return;
        }

        _warehouseProductionStateLoadInProgress = true;
        try
        {
            LoadWarehouseProductionStateCore(search);
        }
        finally
        {
            _warehouseProductionStateLoadInProgress = false;
        }
    }

    private void LoadWarehouseProductionStateCore(string? search)
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
        var addedAny = false;
        foreach (var row in filteredRows)
        {
            if (existingByItemId.TryGetValue(row.ItemId, out var existing))
            {
                existing.ApplyFrom(row);
                nextRows.Add(existing);
                continue;
            }

            addedAny = true;
            nextRows.Add(CreateWarehouseProductionStateDisplayRow(row));
        }

        var removedAny = false;
        for (var index = _warehouseProductionStateRows.Count - 1; index >= 0; index--)
        {
            var itemId = _warehouseProductionStateRows[index].ItemId;
            if (nextRows.All(row => row.ItemId != itemId))
            {
                _warehouseProductionStateRows.RemoveAt(index);
                removedAny = true;
            }
        }

        var orderChanged = SyncWarehouseProductionStateRowOrder(nextRows);
        var structureChanged = removedAny || addedAny || orderChanged;

        UpdateStockEmptyState(search);
        StockGrid.Visibility = Visibility.Collapsed;
        WarehouseProductionStateGrid.Visibility = Visibility.Visible;
        LowStockGrid.Visibility = Visibility.Collapsed;
        LowStockPanel.Visibility = Visibility.Collapsed;
        LowStockSummaryText.Text = string.Empty;

        if (structureChanged)
        {
            RestoreWarehouseProductionStateGridViewState(selectedItemId, scrollOffset);
        }
    }

    private bool SyncWarehouseProductionStateRowOrder(IReadOnlyList<WarehouseProductionStateDisplayRow> nextRows)
    {
        if (IsWarehouseProductionStateOrderUnchanged(nextRows))
        {
            return false;
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

        return true;
    }

    private bool IsWarehouseProductionStateOrderUnchanged(IReadOnlyList<WarehouseProductionStateDisplayRow> nextRows)
    {
        if (_warehouseProductionStateRows.Count != nextRows.Count)
        {
            return false;
        }

        for (var index = 0; index < nextRows.Count; index++)
        {
            if (!ReferenceEquals(_warehouseProductionStateRows[index], nextRows[index]))
            {
                return false;
            }
        }

        return true;
    }

    private WarehouseProductionStateDisplayRow CreateWarehouseProductionStateDisplayRow(WarehouseProductionStateRow row)
    {
        var displayRow = new WarehouseProductionStateDisplayRow { ItemId = row.ItemId };
        var isExpanded = _expandedStockItemIds.Contains(row.ItemId);
        displayRow.IsExpanded = isExpanded;
        displayRow.ExpandMarker = isExpanded ? "▼" : "▶";
        displayRow.ApplyFrom(row);
        if (isExpanded)
        {
            displayRow.EnsureDetailsLoaded();
        }

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
                .Append('|').Append(row.ProductionReceipts.Count);
            foreach (var hu in row.HuRows.OrderBy(current => current.HuCode, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append('|').Append(hu.HuCode)
                    .Append(':').Append(hu.Qty.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(':').Append(hu.StockStatus ?? string.Empty)
                    .Append(':').Append(hu.ReservedCustomerOrderId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                    .Append(':').Append(hu.ReservedCustomerOrderRef ?? string.Empty)
                    .Append(':').Append(hu.ReservedCustomerName ?? string.Empty);
            }

            foreach (var prd in row.ProductionReceipts.OrderBy(current => current.HuCode, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append('|').Append(prd.HuCode)
                    .Append(':').Append(prd.Qty.ToString("F3", CultureInfo.InvariantCulture));
            }

            builder
                .Append('|').Append(row.NeedBreakdown.DemandToCloseCustomerOrders.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.NeedBreakdown.DemandToMinStock.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.NeedBreakdown.AlreadyPlannedInternal.ToString("F3", CultureInfo.InvariantCulture))
                .Append('|').Append(row.NeedBreakdown.RemainingToCreate.ToString("F3", CultureInfo.InvariantCulture))
                .Append(';');
        }

        return builder.ToString();
    }

    private double? GetDataGridVerticalScrollOffset(System.Windows.Controls.DataGrid grid)
    {
        AttachWarehouseProductionStateGridScrollTracking();
        if (ReferenceEquals(grid, WarehouseProductionStateGrid) && _warehouseProductionStateScrollViewer != null)
        {
            return _warehouseProductionStateScrollViewer.VerticalOffset;
        }

        return FindVisualChild<System.Windows.Controls.ScrollViewer>(grid)?.VerticalOffset;
    }

    private void RestoreWarehouseProductionStateGridViewState(long? selectedItemId, double? scrollOffset)
    {
        if (selectedItemId.HasValue)
        {
            var selectedRow = _warehouseProductionStateRows.FirstOrDefault(row => row.ItemId == selectedItemId.Value);
            if (selectedRow != null && !ReferenceEquals(WarehouseProductionStateGrid.SelectedItem, selectedRow))
            {
                WarehouseProductionStateGrid.SelectedItem = selectedRow;
            }
        }

        if (!scrollOffset.HasValue || _stockGridUserScrolling)
        {
            return;
        }

        var targetOffset = scrollOffset.Value;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stockGridUserScrolling)
            {
                return;
            }

            AttachWarehouseProductionStateGridScrollTracking();
            _warehouseProductionStateScrollViewer?.ScrollToVerticalOffset(targetOffset);
        }), DispatcherPriority.Background);
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
        ApplyItemFilters();
    }

    private void ItemsResetSearch_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsSearchBox != null)
        {
            ItemsSearchBox.Text = string.Empty;
        }
        _suppressItemFilterSelectionChanged = true;
        try
        {
            CatalogItemFilter.SetAll(_itemBrandFilters, true);
            CatalogItemFilter.SetAll(_itemVolumeFilters, true);
            CatalogItemFilter.SetAll(_itemUomFilters, true);
        }
        finally
        {
            _suppressItemFilterSelectionChanged = false;
        }

        ApplyItemFilters();
    }

    private void ItemsSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyItemFilters();
        }
    }

    private void ItemBrandFilterAll_Click(object sender, RoutedEventArgs e)
    {
        SetItemFilters(_itemBrandFilters, true);
    }

    private void ItemBrandFilterNone_Click(object sender, RoutedEventArgs e)
    {
        SetItemFilters(_itemBrandFilters, false);
    }

    private void ItemVolumeFilterAll_Click(object sender, RoutedEventArgs e)
    {
        SetItemFilters(_itemVolumeFilters, true);
    }

    private void ItemVolumeFilterNone_Click(object sender, RoutedEventArgs e)
    {
        SetItemFilters(_itemVolumeFilters, false);
    }

    private void ItemUomFilterAll_Click(object sender, RoutedEventArgs e)
    {
        SetItemFilters(_itemUomFilters, true);
    }

    private void ItemUomFilterNone_Click(object sender, RoutedEventArgs e)
    {
        SetItemFilters(_itemUomFilters, false);
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

        if (row is WarehouseProductionStateDisplayRow warehouseRow)
        {
            if (nextExpanded)
            {
                warehouseRow.EnsureDetailsLoaded();
            }
            else
            {
                warehouseRow.ClearDetailRows();
            }
        }
    }

    private void WarehouseProductionStateGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AttachWarehouseProductionStateGridScrollTracking();
        ScrollViewerWheelBubble.HandlePreviewMouseWheel(e, _warehouseProductionStateScrollViewer);
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

    private void HuFillingCorrection_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProductionPalletFillingCorrectionWindow(_services) { Owner = this };
        window.ShowDialog();
        LoadDocs();
        LoadOrders();
        LoadStock(StatusSearchBox.Text);
    }

    private void RefreshHuCorrectionAvailability()
    {
        HuFillingCorrectionButton.IsEnabled =
            _services.WpfAdminApi.TryGetClientBlocks(out var settings)
            && ClientBlockCatalog.MergeWithDefaults(settings)[ClientBlockCatalog.PcHuCorrection];
        HuFillingCorrectionButton.ToolTip = HuFillingCorrectionButton.IsEnabled
            ? null
            : "Функция pc_hu_correction выключена на сервере.";
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
        window.OrderStateChanged += (_, _) => RefreshOrdersKeepingPagedDepth();
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

    private async void OrdersCreateControl_Click(object sender, RoutedEventArgs e)
    {
        var selectedOrders = OrdersGrid.SelectedItems
            .OfType<Order>()
            .ToArray();
        if (selectedOrders.Length == 0)
        {
            MessageBox.Show("Выберите один или несколько заказов.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var orderIds = selectedOrders.Select(order => order.Id).ToArray();
        var preview = await _services.WpfOrderControl.PreviewAsync(orderIds).ConfigureAwait(true);
        if (!preview.IsSuccess)
        {
            MessageBox.Show(preview.ErrorMessage ?? "Не удалось подготовить preview.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new OrderControlPreviewWindow(_services, orderIds, preview)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.Created)
        {
            LoadOrders();
        }
    }

    private void OrderControlWindow_Click(object sender, RoutedEventArgs e)
    {
        var window = new OrderControlWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
        LoadOrders();
    }

    private void HuAssignmentManagement_Click(object sender, RoutedEventArgs e)
    {
        var window = new HuAssignmentManagementWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
        LoadOrders();
        LoadStock(StatusSearchBox.Text);
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
        window.OrderStateChanged += (_, _) => RefreshOrdersKeepingPagedDepth();
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
            ClearItemForm();
            return;
        }

        var item = _items.FirstOrDefault(i => i.Id == itemId.Value);
        if (item == null || !_itemsView.Contains(item))
        {
            ClearItemForm();
            return;
        }

        ItemsGrid.SelectedItem = item;
        ItemsGrid.ScrollIntoView(item);
    }

    private void ItemsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e))
        {
            return;
        }

        e.Handled = true;
        DeleteItem_Click(sender, new RoutedEventArgs());
    }

    private void LocationsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e))
        {
            return;
        }

        e.Handled = true;
        DeleteLocation_Click(sender, new RoutedEventArgs());
    }

    private void PartnersGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e))
        {
            return;
        }

        e.Handled = true;
        DeletePartner_Click(sender, new RoutedEventArgs());
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not Item)
        {
            return;
        }

        EditItem_Click(sender, new RoutedEventArgs());
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

    private bool TryHandleMainGridDeleteGesture()
    {
        if (MainTabs.SelectedIndex == TabItemsIndex
            && ItemsGrid.IsKeyboardFocusWithin
            && GetSelectedItemsForDelete().Count > 0)
        {
            DeleteItem_Click(ItemsGrid, new RoutedEventArgs());
            return true;
        }

        if (MainTabs.SelectedIndex == TabLocationsIndex
            && LocationsGrid.IsKeyboardFocusWithin
            && GetSelectedLocationsForDelete().Count > 0)
        {
            DeleteLocation_Click(LocationsGrid, new RoutedEventArgs());
            return true;
        }

        if (MainTabs.SelectedIndex == TabPartnersIndex
            && PartnersGrid.IsKeyboardFocusWithin
            && GetSelectedPartnersForDelete().Count > 0)
        {
            DeletePartner_Click(PartnersGrid, new RoutedEventArgs());
            return true;
        }

        return false;
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
        var locationsToDelete = GetSelectedLocationsForDelete();
        if (locationsToDelete.Count == 0)
        {
            MessageBox.Show("Выберите место хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmMessage = locationsToDelete.Count == 1
            ? "Удалить выбранное место хранения?"
            : $"Удалить выбранные места хранения ({locationsToDelete.Count})?";
        var confirm = MessageBox.Show(confirmMessage, "Места хранения", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var failed = new List<string>();
            foreach (var location in locationsToDelete)
            {
                try
                {
                    var deleted = await _services.WpfCatalogApi.TryDeleteLocationAsync(location.Id).ConfigureAwait(true);
                    if (!deleted.IsSuccess)
                    {
                        throw new InvalidOperationException(deleted.Error ?? "Не удалось удалить место хранения через сервер.");
                    }
                }
                catch (Exception ex)
                {
                    failed.Add($"{location.Code}: {ex.Message}");
                }
            }

            LoadLocations();
            ClearLocationForm();

            if (failed.Count > 0)
            {
                var message = "Не удалось удалить:\n" + string.Join("\n", failed);
                MessageBox.Show(message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IReadOnlyList<Location> GetSelectedLocationsForDelete()
    {
        if (LocationsGrid.SelectedItems != null && LocationsGrid.SelectedItems.Count > 0)
        {
            return LocationsGrid.SelectedItems.Cast<Location>().ToList();
        }

        return _selectedLocation != null ? new List<Location> { _selectedLocation } : Array.Empty<Location>();
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
        var partnersToDelete = GetSelectedPartnersForDelete();
        if (partnersToDelete.Count == 0)
        {
            MessageBox.Show("Выберите контрагента.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmMessage = partnersToDelete.Count == 1
            ? "Удалить выбранного контрагента?"
            : $"Удалить выбранных контрагентов ({partnersToDelete.Count})?";
        var confirm = MessageBox.Show(confirmMessage, "Контрагенты", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var failed = new List<string>();
            foreach (var partner in partnersToDelete)
            {
                try
                {
                    var deleted = await _services.WpfPartnerApi.TryDeletePartnerAsync(partner.Id).ConfigureAwait(true);
                    if (!deleted.IsSuccess)
                    {
                        throw new InvalidOperationException(deleted.Error ?? "Не удалось удалить контрагента через сервер.");
                    }
                }
                catch (Exception ex)
                {
                    failed.Add($"{partner.Name}: {ex.Message}");
                }
            }

            LoadPartners();
            ClearPartnerForm();

            if (failed.Count > 0)
            {
                var message = "Не удалось удалить:\n" + string.Join("\n", failed);
                MessageBox.Show(message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IReadOnlyList<Partner> GetSelectedPartnersForDelete()
    {
        if (PartnersGrid.SelectedItems != null && PartnersGrid.SelectedItems.Count > 0)
        {
            return PartnersGrid.SelectedItems
                .Cast<PartnerRow>()
                .Select(row => row.Partner)
                .ToList();
        }

        return _selectedPartner != null ? new List<Partner> { _selectedPartner } : Array.Empty<Partner>();
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
        ScheduleItemRequestsBadgeUpdate();
        var window = new IncomingRequestsWindow(_services, () =>
        {
            LoadStock(StatusSearchBox.Text);
            LoadOrders();
            ScheduleItemRequestsBadgeUpdate();
        })
        {
            Owner = this
        };
        window.ShowDialog();
        LoadStock(StatusSearchBox.Text);
        LoadOrders();
        ScheduleItemRequestsBadgeUpdate();
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
                ScheduleItemRequestsBadgeUpdate();
                RefreshHuCorrectionAvailability();
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
            LoadItems();
            LoadStock(StatusSearchBox.Text);
            LoadLowStockView();
        })
        {
            Owner = this
        };
        window.ShowDialog();
        LoadItemTypes();
        LoadItems();
        LoadStock(StatusSearchBox.Text);
        LoadLowStockView();
    }

    private void VatRatesMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new VatRateWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
        LoadItems();
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

    private void PartnerItemPricesMenu_Click(object sender, RoutedEventArgs e)
    {
        new PartnerItemSalePriceWindow(_services)
        {
            Owner = this
        }.ShowDialog();
    }

    private void InitializeCommercialStatisticsFilters()
    {
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            StatisticsStatusesCombo.ItemsSource = _statisticsStatusOptions;
            AttachCommercialStatisticsSearchHandlers();
            RebuildCommercialStatisticsCatalogFilters();
            StatisticsFromDate.SelectedDate = new DateTime(DateTime.Today.Year, 1, 1);
            StatisticsToDate.SelectedDate = DateTime.Today;
            StatisticsModeCombo.SelectedIndex = 0;
            StatisticsGroupCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = false;
        }

        UpdateCommercialStatisticsStatusFilter();
        UpdateCommercialStatisticsNavigation();
    }

    private void AttachCommercialStatisticsSearchHandlers()
    {
        if (_commercialStatisticsSearchHandlersAttached)
        {
            return;
        }

        foreach (var comboBox in GetCommercialStatisticsSearchComboBoxes())
        {
            comboBox.AddHandler(
                System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(StatisticsSearchCombo_TextChanged));
            comboBox.DropDownClosed += StatisticsSearchCombo_DropDownClosed;
        }

        _commercialStatisticsSearchHandlersAttached = true;
    }

    private System.Windows.Controls.ComboBox[] GetCommercialStatisticsSearchComboBoxes() =>
    [
        StatisticsPartnerCombo,
        StatisticsItemCombo,
        StatisticsGtinCombo,
        StatisticsBrandCombo,
        StatisticsVolumeCombo
    ];

    private void RebuildCommercialStatisticsCatalogFilters()
    {
        if (StatisticsPartnerCombo == null)
        {
            return;
        }

        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            _statisticsPartnerOptions = CommercialStatisticsFilterOptions.BuildPartners(
                _partners.Select(row => row.Partner));
            var partnerSelection = CommercialStatisticsFilterOptions.RestoreEntitySelection(
                _statisticsPartnerOptions,
                _statisticsPartnerId);
            _statisticsPartnerId = partnerSelection.Id;
            StatisticsPartnerCombo.ItemsSource = _statisticsPartnerOptions;
            StatisticsPartnerCombo.SelectedItem = partnerSelection;

            _statisticsItemOptions = CommercialStatisticsFilterOptions.BuildItems(_items);
            var itemSelection = CommercialStatisticsFilterOptions.RestoreEntitySelection(
                _statisticsItemOptions,
                _statisticsItemId);
            _statisticsItemId = itemSelection.Id;
            StatisticsItemCombo.ItemsSource = _statisticsItemOptions;
            StatisticsItemCombo.SelectedItem = itemSelection;

            _statisticsGtinOptions = CommercialStatisticsFilterOptions.BuildGtins(_items);
            var gtinSelection = CommercialStatisticsFilterOptions.RestoreTextSelection(
                _statisticsGtinOptions,
                _statisticsGtin);
            _statisticsGtin = gtinSelection.Value;
            StatisticsGtinCombo.ItemsSource = _statisticsGtinOptions;
            StatisticsGtinCombo.SelectedItem = gtinSelection;

            _statisticsBrandOptions = CommercialStatisticsFilterOptions.BuildBrands(_items);
            var brandSelection = CommercialStatisticsFilterOptions.RestoreTextSelection(
                _statisticsBrandOptions,
                _statisticsBrand);
            _statisticsBrand = brandSelection.Value;
            StatisticsBrandCombo.ItemsSource = _statisticsBrandOptions;
            StatisticsBrandCombo.SelectedItem = brandSelection;

            _statisticsVolumeOptions = CommercialStatisticsFilterOptions.BuildVolumes(_items);
            var volumeSelection = CommercialStatisticsFilterOptions.RestoreTextSelection(
                _statisticsVolumeOptions,
                _statisticsVolume);
            _statisticsVolume = volumeSelection.Value;
            StatisticsVolumeCombo.ItemsSource = _statisticsVolumeOptions;
            StatisticsVolumeCombo.SelectedItem = volumeSelection;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }
    }

    private void StatisticsSearchCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || sender is not System.Windows.Controls.ComboBox comboBox
            || !comboBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var query = comboBox.Text ?? string.Empty;
        if (string.Equals(
                GetCommercialStatisticsOptionLabel(comboBox.SelectedItem),
                query,
                StringComparison.Ordinal))
        {
            return;
        }

        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            if (ReferenceEquals(comboBox, StatisticsPartnerCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchEntities(
                    _statisticsPartnerOptions,
                    query);
            }
            else if (ReferenceEquals(comboBox, StatisticsItemCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchEntities(
                    _statisticsItemOptions,
                    query);
            }
            else if (ReferenceEquals(comboBox, StatisticsGtinCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchText(
                    _statisticsGtinOptions,
                    query);
            }
            else if (ReferenceEquals(comboBox, StatisticsBrandCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchText(
                    _statisticsBrandOptions,
                    query);
            }
            else if (ReferenceEquals(comboBox, StatisticsVolumeCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchText(
                    _statisticsVolumeOptions,
                    query);
            }
            else
            {
                return;
            }

            comboBox.SelectedItem = null;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }

        comboBox.IsDropDownOpen = comboBox.Items.Count > 0;
        RestoreCommercialStatisticsComboText(comboBox, query);
    }

    private void StatisticsSearchCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RestoreCommercialStatisticsSearchSelection(comboBox);
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        var candidate = comboBox.SelectedItem ?? comboBox.Items.Cast<object>().FirstOrDefault();
        if (candidate is null)
        {
            return;
        }

        e.Handled = true;
        if (!ReferenceEquals(comboBox.SelectedItem, candidate))
        {
            comboBox.SelectedItem = candidate;
        }
        RestoreCommercialStatisticsSearchSelection(comboBox);
    }

    private void StatisticsSearchCombo_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox)
        {
            return;
        }

        comboBox.Dispatcher.BeginInvoke(() =>
        {
            if (!comboBox.IsKeyboardFocusWithin)
            {
                RestoreCommercialStatisticsSearchSelection(comboBox);
            }
        });
    }

    private void StatisticsSearchCombo_DropDownClosed(object? sender, EventArgs e)
    {
        if (!_suppressCommercialStatisticsFilterEvents
            && sender is System.Windows.Controls.ComboBox comboBox)
        {
            RestoreCommercialStatisticsSearchSelection(comboBox);
        }
    }

    private bool CommitCommercialStatisticsSearchSelection(
        System.Windows.Controls.ComboBox comboBox)
    {
        if (ReferenceEquals(comboBox, StatisticsPartnerCombo)
            && comboBox.SelectedItem is CommercialStatisticsEntityFilterOption partner)
        {
            var changed = _statisticsPartnerId != partner.Id;
            _statisticsPartnerId = partner.Id;
            return changed;
        }
        if (ReferenceEquals(comboBox, StatisticsItemCombo)
            && comboBox.SelectedItem is CommercialStatisticsEntityFilterOption item)
        {
            var changed = _statisticsItemId != item.Id;
            _statisticsItemId = item.Id;
            return changed;
        }
        if (ReferenceEquals(comboBox, StatisticsGtinCombo)
            && comboBox.SelectedItem is CommercialStatisticsTextFilterOption gtin)
        {
            var changed = !string.Equals(
                _statisticsGtin,
                gtin.Value,
                StringComparison.OrdinalIgnoreCase);
            _statisticsGtin = gtin.Value;
            return changed;
        }
        if (ReferenceEquals(comboBox, StatisticsBrandCombo)
            && comboBox.SelectedItem is CommercialStatisticsTextFilterOption brand)
        {
            var changed = !string.Equals(
                _statisticsBrand,
                brand.Value,
                StringComparison.OrdinalIgnoreCase);
            _statisticsBrand = brand.Value;
            return changed;
        }
        if (ReferenceEquals(comboBox, StatisticsVolumeCombo)
            && comboBox.SelectedItem is CommercialStatisticsTextFilterOption volume)
        {
            var changed = !string.Equals(
                _statisticsVolume,
                volume.Value,
                StringComparison.OrdinalIgnoreCase);
            _statisticsVolume = volume.Value;
            return changed;
        }

        return false;
    }

    private void RestoreCommercialStatisticsSearchSelection(
        System.Windows.Controls.ComboBox comboBox)
    {
        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            if (ReferenceEquals(comboBox, StatisticsPartnerCombo))
            {
                comboBox.ItemsSource = _statisticsPartnerOptions;
                comboBox.SelectedItem = CommercialStatisticsFilterOptions.RestoreEntitySelection(
                    _statisticsPartnerOptions,
                    _statisticsPartnerId);
            }
            else if (ReferenceEquals(comboBox, StatisticsItemCombo))
            {
                comboBox.ItemsSource = _statisticsItemOptions;
                comboBox.SelectedItem = CommercialStatisticsFilterOptions.RestoreEntitySelection(
                    _statisticsItemOptions,
                    _statisticsItemId);
            }
            else if (ReferenceEquals(comboBox, StatisticsGtinCombo))
            {
                comboBox.ItemsSource = _statisticsGtinOptions;
                comboBox.SelectedItem = CommercialStatisticsFilterOptions.RestoreTextSelection(
                    _statisticsGtinOptions,
                    _statisticsGtin);
            }
            else if (ReferenceEquals(comboBox, StatisticsBrandCombo))
            {
                comboBox.ItemsSource = _statisticsBrandOptions;
                comboBox.SelectedItem = CommercialStatisticsFilterOptions.RestoreTextSelection(
                    _statisticsBrandOptions,
                    _statisticsBrand);
            }
            else if (ReferenceEquals(comboBox, StatisticsVolumeCombo))
            {
                comboBox.ItemsSource = _statisticsVolumeOptions;
                comboBox.SelectedItem = CommercialStatisticsFilterOptions.RestoreTextSelection(
                    _statisticsVolumeOptions,
                    _statisticsVolume);
            }

            comboBox.Text = GetCommercialStatisticsOptionLabel(comboBox.SelectedItem) ?? string.Empty;
            comboBox.IsDropDownOpen = false;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }
    }

    private static string? GetCommercialStatisticsOptionLabel(object? option) =>
        option switch
        {
            CommercialStatisticsEntityFilterOption entity => entity.Label,
            CommercialStatisticsTextFilterOption text => text.Label,
            _ => null
        };

    private static void RestoreCommercialStatisticsComboText(
        System.Windows.Controls.ComboBox comboBox,
        string text)
    {
        comboBox.Dispatcher.BeginInvoke(() =>
        {
            if (comboBox.Template.FindName(
                    "PART_EditableTextBox",
                    comboBox) is System.Windows.Controls.TextBox textBox)
            {
                if (!string.Equals(textBox.Text, text, StringComparison.Ordinal))
                {
                    textBox.Text = text;
                }

                textBox.CaretIndex = textBox.Text.Length;
            }
        });
    }

    private void UpdateCommercialStatisticsStatusFilter()
    {
        if (StatisticsStatusesCombo == null)
        {
            return;
        }

        var mode = (StatisticsModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "orders";
        StatisticsStatusesCombo.IsEnabled =
            string.Equals(mode, "orders", StringComparison.OrdinalIgnoreCase);
        StatisticsStatusesCombo.Text =
            CommercialStatisticsFilterOptions.BuildStatusesLabel(_statisticsStatusOptions);
    }

    private void StatisticsCriteria_Changed(object sender, EventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents || !IsLoaded)
        {
            return;
        }

        if (sender is System.Windows.Controls.ComboBox comboBox
            && IsCommercialStatisticsSearchComboBox(comboBox)
            && !CommitCommercialStatisticsSearchSelection(comboBox))
        {
            return;
        }

        UpdateCommercialStatisticsStatusFilter();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void StatisticsStatusOption_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents)
        {
            return;
        }

        if (_statisticsStatusOptions.All(option => !option.IsChecked))
        {
            foreach (var option in _statisticsStatusOptions)
            {
                option.IsChecked = true;
            }
        }

        UpdateCommercialStatisticsStatusFilter();
        StatisticsCriteria_Changed(sender, e);
    }

    private void StatisticsPeriod_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        StatisticsMonthlyGrid.SelectedItem = null;
        _commercialStatisticsState.CriteriaChanged(periodChanged: true);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private async void StatisticsPreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_commercialStatisticsState.MovePrevious())
        {
            await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
        }
    }

    private async void StatisticsNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_commercialStatisticsState.MoveNext())
        {
            await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
        }
    }

    private async void StatisticsMonthlyGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded
            || StatisticsMonthlyGrid.SelectedItem is not WpfCommercialStatisticsMonth month)
        {
            return;
        }

        if (string.Equals(
                _commercialStatisticsState.DetailMonth,
                month.Month,
                StringComparison.Ordinal))
        {
            return;
        }

        _commercialStatisticsState.SelectDetailMonth(month.Month);
        UpdateCommercialStatisticsNavigation();
        await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
    }

    private async void StatisticsAllPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (!_commercialStatisticsState.ReturnToWholePeriod())
        {
            return;
        }

        StatisticsMonthlyGrid.SelectedItem = null;
        UpdateCommercialStatisticsNavigation();
        await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
    }

    private bool IsCommercialStatisticsSearchComboBox(
        System.Windows.Controls.ComboBox comboBox) =>
        ReferenceEquals(comboBox, StatisticsPartnerCombo)
        || ReferenceEquals(comboBox, StatisticsItemCombo)
        || ReferenceEquals(comboBox, StatisticsGtinCombo)
        || ReferenceEquals(comboBox, StatisticsBrandCombo)
        || ReferenceEquals(comboBox, StatisticsVolumeCombo);

    private void ScheduleCommercialStatisticsRefresh()
    {
        if (!IsLoaded || MainTabs.SelectedIndex != TabStatisticsIndex)
        {
            return;
        }

        _commercialStatisticsAutoRefresh.Schedule();
        _commercialStatisticsRefreshTimer ??= new DispatcherTimer
        {
            Interval = CommercialStatisticsRefreshDebounceInterval
        };
        _commercialStatisticsRefreshTimer.Tick -= CommercialStatisticsRefreshTimer_Tick;
        _commercialStatisticsRefreshTimer.Tick += CommercialStatisticsRefreshTimer_Tick;
        _commercialStatisticsRefreshTimer.Stop();
        _commercialStatisticsRefreshTimer.Start();
    }

    private async void CommercialStatisticsRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _commercialStatisticsRefreshTimer?.Stop();
        if (!_commercialStatisticsAutoRefresh.TryConsume(out _))
        {
            return;
        }

        _commercialStatisticsInitialLoadStarted = true;
        await LoadCommercialStatisticsAsync().ConfigureAwait(true);
    }

    private void CancelScheduledCommercialStatisticsRefresh()
    {
        _commercialStatisticsRefreshTimer?.Stop();
        _commercialStatisticsAutoRefresh.Cancel();
    }

    private async Task LoadCommercialStatisticsImmediatelyAsync()
    {
        CancelScheduledCommercialStatisticsRefresh();
        _commercialStatisticsInitialLoadStarted = true;
        await LoadCommercialStatisticsAsync().ConfigureAwait(true);
    }

    private async Task LoadCommercialStatisticsAsync()
    {
        if (StatisticsFromDate.SelectedDate is not DateTime from
            || StatisticsToDate.SelectedDate is not DateTime to)
        {
            StatisticsKpiText.Text = "Укажите корректный период статистики.";
            StatisticsQualityText.Text = string.Empty;
            return;
        }
        if (to < from)
        {
            StatisticsKpiText.Text = "Дата окончания должна быть не раньше даты начала.";
            StatisticsQualityText.Text = string.Empty;
            return;
        }
        var mode = (StatisticsModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "orders";
        var groupBy = (StatisticsGroupCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "partner";
        var load = _commercialStatisticsState.StartLoad(
            new WpfCommercialStatisticsFilters(
                mode,
                groupBy,
                from,
                to,
                _statisticsPartnerId,
                _statisticsItemId,
                _statisticsGtin,
                _statisticsBrand,
                _statisticsVolume,
                CommercialStatisticsFilterOptions.BuildStatusesCsv(
                    mode,
                    _statisticsStatusOptions),
                Sort: "gross_desc"));
        UpdateCommercialStatisticsNavigation();
        StatisticsKpiText.Text = "Загрузка...";
        try
        {
            var result = await _services.WpfCommercialStatisticsApi.GetAsync(
                load.Request).ConfigureAwait(true);
            if (!_commercialStatisticsState.TryComplete(load.RequestId, result))
            {
                return;
            }

            StatisticsMonthlyGrid.ItemsSource = result.Monthly;
            StatisticsGroupsGrid.ItemsSource = result.Groups.Items;
            StatisticsKpiText.Text =
                $"Количество: {result.Summary.Quantity:0.######}; с НДС: {result.Summary.Gross:N2}; без НДС: {result.Summary.Net:N2}; НДС: {result.Summary.Vat:N2}";
            StatisticsQualityText.Text =
                CommercialStatisticsDataQualityPresentation.Format(result.DataQuality);
        }
        catch (Exception ex)
        {
            if (!_commercialStatisticsState.TryFail(load.RequestId))
            {
                return;
            }

            StatisticsMonthlyGrid.ItemsSource = null;
            StatisticsGroupsGrid.ItemsSource = null;
            StatisticsKpiText.Text = "Не удалось загрузить статистику.";
            StatisticsQualityText.Text = ex.Message;
        }
        finally
        {
            UpdateCommercialStatisticsNavigation();
        }
    }

    private void UpdateCommercialStatisticsNavigation()
    {
        if (StatisticsPreviousPageButton == null)
        {
            return;
        }

        StatisticsPreviousPageButton.IsEnabled = _commercialStatisticsState.CanMovePrevious;
        StatisticsNextPageButton.IsEnabled = _commercialStatisticsState.CanMoveNext;
        StatisticsAllPeriodButton.IsEnabled = _commercialStatisticsState.CanReturnToWholePeriod;
        StatisticsPageText.Text = _commercialStatisticsState.RangeText;
        StatisticsGroupsBox.Header = _commercialStatisticsState.DetailLabel;
        UpdateCommercialStatisticsStatusFilter();
    }

    // Legacy: отдельное окно/очередь "Маркировка" больше не выводится в главное меню WPF.
    // Обработчик и MarkingWindow сохраняются для совместимости и возможной диагностики.
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
    private void StartItemRequestsBadgeRefreshTimer()
    {
        if (_itemRequestsBadgeRefreshTimer != null)
        {
            return;
        }

        _itemRequestsBadgeRefreshTimer = new DispatcherTimer
        {
            Interval = ItemRequestsBadgeRefreshInterval
        };
        _itemRequestsBadgeRefreshTimer.Tick += (_, _) => ScheduleItemRequestsBadgeUpdate();
        _itemRequestsBadgeRefreshTimer.Start();
    }

    private void ScheduleItemRequestsBadgeUpdate()
    {
        if (_itemRequestsBadgeUpdateInProgress)
        {
            _itemRequestsBadgeUpdatePending = true;
            return;
        }

        _ = RefreshItemRequestsBadgeAsync();
    }

    private async Task RefreshItemRequestsBadgeAsync()
    {
        _itemRequestsBadgeUpdateInProgress = true;
        try
        {
            do
            {
                _itemRequestsBadgeUpdatePending = false;
                var summary = await Task.Run(() =>
                    _services.WpfIncomingRequestsApi.TryGetSummary(out var apiSummary)
                        ? apiSummary
                        : new IncomingRequestsSummary(0, 0, 0));

                ApplyItemRequestsBadgeSummary(summary);
            } while (_itemRequestsBadgeUpdatePending);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Incoming requests badge update failed", ex);
        }
        finally
        {
            _itemRequestsBadgeUpdateInProgress = false;
        }
    }

    private void ApplyItemRequestsBadgeSummary(IncomingRequestsSummary summary)
    {
        if (ItemRequestsBadge == null || ItemRequestsCountText == null)
        {
            return;
        }

        var itemCount = summary.ItemRequestsPending;
        var orderCount = summary.OrderRequestsPending;
        var notificationCount = summary.BusinessNotificationsUnread;
        var count = summary.TotalPending;
        ItemRequestsCountText.Text = count.ToString();
        ItemRequestsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemRequestsButton.ToolTip = count > 0
            ? BuildIncomingRequestsTooltip(count, itemCount, orderCount, notificationCount)
            : "Центр событий";
    }

    private static string BuildIncomingRequestsTooltip(int totalCount, int itemCount, int orderCount, int notificationCount)
    {
        var parts = new List<string>
        {
            $"товары: {itemCount}",
            $"заказы: {orderCount}"
        };
        if (notificationCount > 0)
        {
            parts.Add($"новые события: {notificationCount}");
        }

        return $"Центр событий: {totalCount} ({string.Join(", ", parts)})";
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
            "CORRECTED" => "Скорректирована",
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
        private static readonly IReadOnlyList<WarehouseProductionStateHuDisplayRow> EmptyHuRows = Array.Empty<WarehouseProductionStateHuDisplayRow>();
        private static readonly IReadOnlyList<WarehouseProductionStatePalletDisplayRow> EmptyProductionReceipts = Array.Empty<WarehouseProductionStatePalletDisplayRow>();

        private bool _isExpanded;
        private string _expandMarker = "▶";
        private WarehouseProductionStateRow? _sourceRow;
        private string _summaryFingerprint = string.Empty;
        private string _detailsFingerprint = string.Empty;
        private bool _detailsLoaded;

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
            _sourceRow = row;
            var summaryFingerprint = BuildSummaryFingerprint(row);
            if (!string.Equals(summaryFingerprint, _summaryFingerprint, StringComparison.Ordinal))
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
                _summaryFingerprint = summaryFingerprint;
                NotifySummaryPropertiesChanged();
            }

            UpdateNeedBreakdownRows(row);

            if (IsExpanded)
            {
                EnsureDetailsLoaded();
            }
            else if (_detailsLoaded)
            {
                ClearDetailRows();
            }
        }

        public void EnsureDetailsLoaded()
        {
            if (_sourceRow == null)
            {
                return;
            }

            var detailsFingerprint = BuildDetailFingerprint(_sourceRow);
            if (_detailsLoaded && string.Equals(detailsFingerprint, _detailsFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            LoadDetailRows(_sourceRow);
            _detailsFingerprint = detailsFingerprint;
            _detailsLoaded = true;
            NotifyDetailPropertiesChanged();
        }

        public void ClearDetailRows()
        {
            if (!_detailsLoaded
                && ReferenceEquals(WarehouseHuRows, EmptyHuRows)
                && ReferenceEquals(ProductionReceipts, EmptyProductionReceipts))
            {
                return;
            }

            WarehouseHuRows = EmptyHuRows;
            ProductionReceipts = EmptyProductionReceipts;
            CustomerOrders = Array.Empty<WarehouseProductionStateCustomerOrderDisplayRow>();
            InternalOrders = Array.Empty<WarehouseProductionStateInternalOrderDisplayRow>();
            _detailsLoaded = false;
            _detailsFingerprint = string.Empty;
            NotifyDetailPropertiesChanged();
        }

        private void UpdateNeedBreakdownRows(WarehouseProductionStateRow row)
        {
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedBreakdownRows)));
        }

        private void LoadDetailRows(WarehouseProductionStateRow row)
        {
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
        }

        private static string BuildSummaryFingerprint(WarehouseProductionStateRow row)
        {
            return string.Create(CultureInfo.InvariantCulture, $"""
                {row.ItemName}|{row.Barcode}|{row.Gtin}|{row.ItemType}|{row.Brand}|{row.BaseUom}|
                {row.StockQty:F3}|{row.FreeQty:F3}|{row.ReservedQty:F3}|{row.MinStockQty:F3}|{row.BelowMinQty:F3}|
                {row.CustomerOpenDemandQty:F3}|{row.PrdPlannedQty:F3}|{row.PrdFilledQty:F3}|{row.InternalRemainingQty:F3}|{row.RemainingNeedQty:F3}|
                {row.NeedReason}|{row.HuRows.Count}|{row.ProductionReceipts.Count}|
                {row.NeedBreakdown.DemandToCloseCustomerOrders:F3}|{row.NeedBreakdown.DemandToMinStock:F3}|{row.NeedBreakdown.AlreadyPlannedInternal:F3}|{row.NeedBreakdown.RemainingToCreate:F3}
                """);
        }

        private static string BuildDetailFingerprint(WarehouseProductionStateRow row)
        {
            var builder = new StringBuilder(256);
            foreach (var hu in row.HuRows.OrderBy(current => current.HuCode, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(hu.HuCode)
                    .Append('|').Append(hu.Location)
                    .Append('|').Append(hu.Qty.ToString("F3", CultureInfo.InvariantCulture))
                    .Append('|').Append(hu.StockStatus)
                    .Append(';');
            }

            builder.Append('#');
            foreach (var prd in row.ProductionReceipts.OrderBy(current => current.HuCode, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(prd.HuCode)
                    .Append('|').Append(prd.PrdRef)
                    .Append('|').Append(prd.PalletStatus)
                    .Append('|').Append(prd.Qty.ToString("F3", CultureInfo.InvariantCulture))
                    .Append('|').Append(prd.PlannedQty.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(';');
            }

            return builder.ToString();
        }

        private void NotifySummaryPropertiesChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Barcode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gtin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemTypeName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brand)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BaseUom)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StockQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreeQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReservedQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinStockQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BelowMinQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomerOpenDemandQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrdPlannedQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrdFilledQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternalRemainingQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingNeedQty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedReason)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Warnings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBelowMin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StockQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreeQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReservedQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinStockQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BelowMinQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomerOpenDemandQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrdPlannedQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrdFilledQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingNeedQtyDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProductSubline)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinStockSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlanSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilledSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingNeedSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingNeedBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingNeedFontWeight)));
        }

        private void NotifyDetailPropertiesChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WarehouseHuRows)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomerOrders)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternalOrders)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProductionReceipts)));
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

}
