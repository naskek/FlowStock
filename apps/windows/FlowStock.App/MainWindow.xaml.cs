using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExcelDataReader;
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
    private readonly ObservableCollection<StockDisplayRow> _stock = new();
    private readonly ObservableCollection<LowStockDisplayRow> _lowStock = new();
    private readonly ObservableCollection<StockLocationFilterOption> _stockLocationFilters = new();
    private readonly ObservableCollection<StockHuFilterOption> _stockHuFilters = new();
    private readonly ObservableCollection<StockItemTypeFilterOption> _stockItemTypeFilters = new();
    private readonly ObservableCollection<KmCodeBatch> _kmBatches = new();
    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _autoRefreshInProgress;
    private static bool _excelEncodingRegistered;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(20);
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
    private const int TabLowStockIndex = 1;
    private const int TabDocsIndex = 2;
    private const int TabOrdersIndex = 3;
    private const int TabItemsIndex = 4;
    private const int TabLocationsIndex = 5;
    private const int TabPartnersIndex = 6;
    private const int TabKmIndex = 7;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        LocationsGrid.ItemsSource = _locations;
        PartnersGrid.ItemsSource = _partners;
        DocsGrid.ItemsSource = _docs;
        OrdersGrid.ItemsSource = _orders;
        StockGrid.ItemsSource = _stock;
        LowStockGrid.ItemsSource = _lowStock;
        StockLocationFilter.ItemsSource = _stockLocationFilters;
        StockHuFilter.ItemsSource = _stockHuFilters;
        StockItemTypeFilter.ItemsSource = _stockItemTypeFilters;
        KmBatchesGrid.ItemsSource = _kmBatches;
        DocsTypeFilter.ItemsSource = _docTypeFilters;
        DocsTypeFilter.SelectedIndex = 0;
        DocsStatusFilter.ItemsSource = _docStatusFilters;
        DocsStatusFilter.SelectedIndex = 0;
        ApplyDeleteMode();

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

    private void UpdateDeleteButtonsAvailability()
    {
        if (ItemDeleteButton != null)
        {
            var hasSelection = _selectedItem != null
                               || (ItemsGrid?.SelectedItems?.Count ?? 0) > 0;
            ItemDeleteButton.IsEnabled = _adminDeleteModeEnabled && hasSelection;
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
            LocationDeleteButton.IsEnabled = _adminDeleteModeEnabled && _selectedLocation != null;
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

        if (OrdersDeleteButton != null)
        {
            OrdersDeleteButton.IsEnabled = _adminDeleteModeEnabled && OrdersGrid.SelectedItem is Order;
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
        _autoRefreshTimer.Start();
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
                    LoadStockHuFilters();
                    LoadStock(StatusSearchBox.Text);
                    break;
                case TabLowStockIndex:
                    LoadLowStockView();
                    break;
                case TabDocsIndex:
                    LoadDocs();
                    break;
                case TabOrdersIndex:
                    LoadOrders();
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
                && ItemsGrid.SelectedItems.Count > 0
                && _adminDeleteModeEnabled)
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

    private void LoadStockHuFilters()
    {
        var selectedCode = GetSelectedStockHuCode();
        _stockHuFilters.Clear();
        _stockHuFilters.Add(new StockHuFilterOption(null, "Все HU"));

        var availableHuCodes = GetAvailableHuCodesForFilter();
        foreach (var hu in availableHuCodes)
        {
            _stockHuFilters.Add(new StockHuFilterOption(hu, hu));
        }

        var selected = _stockHuFilters.FirstOrDefault(option => string.Equals(option.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
                       ?? _stockHuFilters.FirstOrDefault();
        StockHuFilter.SelectedItem = selected;
    }

    private IEnumerable<string> GetAvailableHuCodesForFilter()
    {
        var locationCode = GetSelectedStockLocationCode();
        var rows = _services.WpfReadApi.TryGetStockRows(null, out var apiRows)
            ? apiRows
            : Array.Empty<StockRow>();
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

    private void LoadOrders()
    {
        var selectedId = (OrdersGrid.SelectedItem as Order)?.Id;
        _orders.Clear();
        var orders = _services.WpfReadApi.TryGetOrders(includeInternal: true, search: null, out var apiOrders)
            ? apiOrders
            : Array.Empty<Order>();
        foreach (var order in orders)
        {
            _orders.Add(order);
        }
        RestoreOrderSelection(selectedId);
        UpdateDeleteButtonsAvailability();
    }

    private void LoadStock(string? search)
    {
        _stock.Clear();
        var locationCode = GetSelectedStockLocationCode();
        var huCode = GetSelectedStockHuCode();
        var itemTypeId = GetSelectedStockItemTypeId();
        var belowMinOnly = StockBelowMinOnlyCheckBox.IsChecked == true;
        var rows = _services.WpfReadApi.TryGetStockRows(search, out var apiRows)
            ? apiRows
            : Array.Empty<StockRow>();
        var lowStockByItem = BuildLowStockByItem(rows);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(locationCode)
                && !string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(huCode)
                && !string.Equals(row.Hu, huCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (itemTypeId.HasValue && row.ItemTypeId != itemTypeId.Value)
            {
                continue;
            }

            var isBelowMin = lowStockByItem.ContainsKey(row.ItemId);
            if (belowMinOnly && !isBelowMin)
            {
                continue;
            }

            var packaging = _services.Packagings.FormatAsPackaging(row.ItemId, row.Qty);
            var baseDisplay = $"{FormatQty(row.Qty)} {row.BaseUom}";
            _stock.Add(new StockDisplayRow
            {
                ItemId = row.ItemId,
                ItemName = row.ItemName,
                ItemTypeName = row.ItemTypeName ?? "Без типа",
                Barcode = row.Barcode,
                LocationCode = row.LocationCode,
                HuDisplay = row.Hu ?? string.Empty,
                PackagingDisplay = packaging,
                BaseDisplay = baseDisplay,
                IsBelowMin = isBelowMin
            });
        }

        UpdateStockEmptyState(search);
        if (MainTabs.SelectedIndex != TabLowStockIndex)
        {
            LoadLowStockView();
        }
    }

    private void UpdateStockEmptyState(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)
            && _stock.Count == 0
            && string.IsNullOrWhiteSpace(GetSelectedStockLocationCode())
            && string.IsNullOrWhiteSpace(GetSelectedStockHuCode())
            && !GetSelectedStockItemTypeId().HasValue
            && StockBelowMinOnlyCheckBox.IsChecked != true)
        {
            StockEmptyText.Visibility = Visibility.Visible;
            return;
        }

        StockEmptyText.Visibility = Visibility.Collapsed;
    }

    private Dictionary<long, LowStockSnapshot> BuildLowStockByItem(IReadOnlyList<StockRow> rows)
    {
        return rows
            .GroupBy(row => row.ItemId)
            .Select(group =>
            {
                var first = group.First();
                var totalQty = group.Sum(row => row.Qty);
                var minStockQty = first.MinStockQty;
                var isBelow = first.ItemTypeEnableMinStockControl
                              && minStockQty.HasValue
                              && totalQty < minStockQty.Value;
                return new LowStockSnapshot(
                    group.Key,
                    first.ItemName,
                    first.ItemTypeName ?? "Без типа",
                    totalQty,
                    minStockQty,
                    isBelow);
            })
            .Where(snapshot => snapshot.IsBelowMin)
            .ToDictionary(snapshot => snapshot.ItemId, snapshot => snapshot);
    }

    private void LoadLowStockView()
    {
        _lowStock.Clear();
        var rows = _services.WpfReadApi.TryGetStockRows(null, out var apiRows)
            ? apiRows
            : Array.Empty<StockRow>();
        var lowStockByItem = BuildLowStockByItem(rows);
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
                QtyDisplay = FormatQty(snapshot.Qty),
                MinStockQtyDisplay = FormatQty(snapshot.MinStockQty.GetValueOrDefault()),
                ShortageDisplay = FormatQty(shortage > 0 ? shortage : 0)
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
        LoadStockHuFilters();
        LoadStock(StatusSearchBox.Text);
    }

    private void StockHuFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private void StockItemTypeFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private void StockBelowMinOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private string? GetSelectedStockLocationCode()
    {
        return (StockLocationFilter.SelectedItem as StockLocationFilterOption)?.Code;
    }

    private string? GetSelectedStockHuCode()
    {
        return (StockHuFilter.SelectedItem as StockHuFilterOption)?.Code;
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

    private void OpenDocDetails(Doc doc)
    {
        try
        {
            var wasClosed = doc.Status == DocStatus.Closed;
            var window = new OperationDetailsWindow(_services, doc.Id)
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

    private void OrdersDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeleteModeEnabled("Заказы"))
        {
            return;
        }

        if (OrdersGrid.SelectedItem is not Order order)
        {
            MessageBox.Show("Выберите заказ.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранный заказ?", "Заказы", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = _services.WpfDeleteOrders.DeleteOrderAsync(order.Id)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!result.IsSuccess)
            {
                var icon = result.Kind is WpfDeleteOrderResultKind.Timeout or WpfDeleteOrderResultKind.ServerUnavailable
                    ? MessageBoxImage.Error
                    : MessageBoxImage.Warning;
                MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, icon);
                return;
            }

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
        if (e.Key != Key.Delete || !_adminDeleteModeEnabled)
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
        if (!EnsureDeleteModeEnabled("Товары"))
        {
            return;
        }

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

    private async void DeleteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeleteModeEnabled("Места хранения"))
        {
            return;
        }

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
            OpenDocDetails(created);
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

    private sealed record DocTypeFilterOption(DocType? Type, string Name);

    private sealed record DocStatusFilterOption(DocStatus? Status, string Name);

    private sealed record PartnerRow(Partner Partner, string StatusDisplay)
    {
        public long Id => Partner.Id;
        public string Name => Partner.Name;
        public string? Code => Partner.Code;
        public DateTime CreatedAt => Partner.CreatedAt;
    }

    private sealed record StockDisplayRow
    {
        public long ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public string LocationCode { get; init; } = string.Empty;
        public string HuDisplay { get; init; } = string.Empty;
        public string PackagingDisplay { get; init; } = string.Empty;
        public string BaseDisplay { get; init; } = string.Empty;
        public bool IsBelowMin { get; init; }
    }

    private sealed record StockLocationFilterOption(string? Code, string Name);

    private sealed record StockHuFilterOption(string? Code, string Name);

    private sealed record StockItemTypeFilterOption(long? Id, string Name);

    private sealed record LowStockSnapshot(long ItemId, string ItemName, string ItemTypeName, double Qty, double? MinStockQty, bool IsBelowMin);

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

