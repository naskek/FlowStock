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
    private readonly ObservableCollection<PartnerRow> _partners = new();
    private readonly ObservableCollection<Doc> _docs = new();
    private readonly ObservableCollection<Order> _orders = new();
    private readonly ObservableCollection<StockDisplayRow> _stock = new();
    private readonly ObservableCollection<StockLocationFilterOption> _stockLocationFilters = new();
    private readonly ObservableCollection<StockHuFilterOption> _stockHuFilters = new();
    private readonly ObservableCollection<PackagingOption> _itemPackagingOptions = new();
    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _autoRefreshInProgress;
    private static bool _excelEncodingRegistered;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(20);
    private readonly List<PartnerStatusOption> _partnerStatusOptions = new()
    {
        new PartnerStatusOption(PartnerStatus.Supplier, "Поставщик"),
        new PartnerStatusOption(PartnerStatus.Client, "Клиент"),
        new PartnerStatusOption(PartnerStatus.Both, "Клиент и поставщик")
    };
    private readonly List<DocTypeFilterOption> _docTypeFilters = new()
    {
        new DocTypeFilterOption(null, "Все"),
        new DocTypeFilterOption(DocType.Inbound, "Приемка"),
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
    private bool _suppressPackagingSelection;
    private const int TabStatusIndex = 0;
    private const int TabDocsIndex = 1;
    private const int TabOrdersIndex = 2;
    private const int TabItemsIndex = 3;
    private const int TabLocationsIndex = 4;
    private const int TabPartnersIndex = 5;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        LocationsGrid.ItemsSource = _locations;
        ItemUomCombo.ItemsSource = _uoms;
        ItemDisplayUomCombo.ItemsSource = _itemPackagingOptions;
        PartnersGrid.ItemsSource = _partners;
        PartnerStatusCombo.ItemsSource = _partnerStatusOptions;
        DocsGrid.ItemsSource = _docs;
        OrdersGrid.ItemsSource = _orders;
        StockGrid.ItemsSource = _stock;
        StockLocationFilter.ItemsSource = _stockLocationFilters;
        StockHuFilter.ItemsSource = _stockHuFilters;
        DocsTypeFilter.ItemsSource = _docTypeFilters;
        DocsTypeFilter.SelectedIndex = 0;
        DocsStatusFilter.ItemsSource = _docStatusFilters;
        DocsStatusFilter.SelectedIndex = 0;
        SetPartnerStatusSelection(PartnerStatus.Both);

        LoadAll();
        ClearItemForm();
        ClearLocationForm();
        ClearPartnerForm();

        _autoRefreshTimer = new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void LoadAll()
    {
        LoadItems();
        LoadUoms();
        LoadLocations();
        LoadPartners();
        LoadDocs();
        LoadOrders();
        LoadStock(null);
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
                    LoadStockHuFilters();
                    LoadStock(StatusSearchBox.Text);
                    break;
                case TabDocsIndex:
                    LoadDocs();
                    break;
                case TabOrdersIndex:
                    LoadOrders();
                    break;
                case TabItemsIndex:
                    if (!IsItemFormEditing())
                    {
                        LoadItems(ItemsSearchBox?.Text);
                    }
                    break;
                case TabLocationsIndex:
                    if (!IsLocationFormEditing())
                    {
                        LoadLocations();
                    }
                    break;
                case TabPartnersIndex:
                    if (!IsPartnerFormEditing())
                    {
                        LoadPartners();
                    }
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

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
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
                TryCloseSelectedDoc();
                break;
        }
    }

    private void LoadItems(string? search = null)
    {
        var selectedId = _selectedItem?.Id;
        _items.Clear();
        var query = search ?? ItemsSearchBox?.Text;
        var normalized = NormalizeIdentifier(query);
        foreach (var item in _services.Catalog.GetItems(normalized))
        {
            _items.Add(item);
        }
        RestoreItemSelection(selectedId);
    }

    private void LoadUoms()
    {
        _uoms.Clear();
        foreach (var uom in _services.Catalog.GetUoms())
        {
            _uoms.Add(uom);
        }
    }

    private void LoadItemPackagingOptions(Item? item)
    {
        _itemPackagingOptions.Clear();
        ItemDisplayUomCombo.IsEnabled = item != null;
        ItemPackagingButton.IsEnabled = item != null;

        if (item == null)
        {
            ItemDisplayUomCombo.SelectedItem = null;
            return;
        }

        _itemPackagingOptions.Add(new PackagingOption(null, $"В базе ({item.BaseUom})"));
        foreach (var packaging in _services.Packagings.GetPackagings(item.Id))
        {
            _itemPackagingOptions.Add(new PackagingOption(packaging.Id, $"{packaging.Name} ({packaging.Code})"));
        }

        _suppressPackagingSelection = true;
        ItemDisplayUomCombo.SelectedItem = _itemPackagingOptions.FirstOrDefault(option => option.PackagingId == item.DefaultPackagingId)
                                           ?? _itemPackagingOptions.FirstOrDefault();
        _suppressPackagingSelection = false;
    }

    private void ReloadItemsAndSelect(long itemId)
    {
        LoadItems();
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return;
        }

        ItemsGrid.SelectedItem = item;
        ItemsGrid.ScrollIntoView(item);
    }

    private void LoadLocations()
    {
        var selectedId = _selectedLocation?.Id;
        _locations.Clear();
        foreach (var location in _services.Catalog.GetLocations())
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
        foreach (var location in _services.Catalog.GetLocations())
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
        if (!string.IsNullOrWhiteSpace(locationCode))
        {
            var location = _locations.FirstOrDefault(item =>
                string.Equals(item.Code, locationCode, StringComparison.OrdinalIgnoreCase));
            if (location == null)
            {
                return Enumerable.Empty<string>();
            }

            return _services.DataStore.GetHuCodesByLocation(location.Id)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return _services.DataStore.GetLedgerTotalsByHu()
            .Where(entry => entry.Value > 0.000001)
            .Select(entry => entry.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadPartners()
    {
        var selectedId = _selectedPartner?.Id;
        _partners.Clear();
        foreach (var partner in _services.Catalog.GetPartners())
        {
            var status = _services.PartnerStatuses.GetStatus(partner.Id);
            _partners.Add(new PartnerRow(partner, GetPartnerStatusLabel(status)));
        }
        RestorePartnerSelection(selectedId);
    }

    private void LoadDocs()
    {
        var selectedId = (DocsGrid.SelectedItem as Doc)?.Id;
        _docs.Clear();
        foreach (var doc in ApplyDocFilters(_services.Documents.GetDocs()))
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
        foreach (var order in _services.Orders.GetOrders())
        {
            _orders.Add(order);
        }
        RestoreOrderSelection(selectedId);
    }

    private void LoadStock(string? search)
    {
        _stock.Clear();
        var locationCode = GetSelectedStockLocationCode();
        var huCode = GetSelectedStockHuCode();
        foreach (var row in _services.Documents.GetStock(search))
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

            var packaging = _services.Packagings.FormatAsPackaging(row.ItemId, row.Qty);
            var baseDisplay = $"{FormatQty(row.Qty)} {row.BaseUom}";
            _stock.Add(new StockDisplayRow
            {
                ItemName = row.ItemName,
                Barcode = row.Barcode,
                LocationCode = row.LocationCode,
                HuDisplay = row.Hu ?? string.Empty,
                PackagingDisplay = packaging,
                BaseDisplay = baseDisplay
            });
        }

        UpdateStockEmptyState(search);
    }

    private void UpdateStockEmptyState(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)
            && _stock.Count == 0
            && string.IsNullOrWhiteSpace(GetSelectedStockLocationCode())
            && string.IsNullOrWhiteSpace(GetSelectedStockHuCode()))
        {
            StockEmptyText.Visibility = Visibility.Visible;
            return;
        }

        StockEmptyText.Visibility = Visibility.Collapsed;
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

    private string? GetSelectedStockLocationCode()
    {
        return (StockLocationFilter.SelectedItem as StockLocationFilterOption)?.Code;
    }

    private string? GetSelectedStockHuCode()
    {
        return (StockHuFilter.SelectedItem as StockHuFilterOption)?.Code;
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
        var wasClosed = doc.Status == DocStatus.Closed;
        var window = new OperationDetailsWindow(_services, doc.Id)
        {
            Owner = this
        };
        window.ShowDialog();

        LoadDocs();
        var refreshed = _services.Documents.GetDoc(doc.Id);
        if (!wasClosed && refreshed?.Status == DocStatus.Closed)
        {
            LoadStock(StatusSearchBox.Text);
            if (refreshed.Type == DocType.Outbound)
            {
                LoadOrders();
            }
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
            _services.Orders.DeleteOrder(order.Id);
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

    private void DocClose_Click(object sender, RoutedEventArgs e)
    {
        TryCloseSelectedDoc();
    }

    private void TryCloseSelectedDoc()
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

        var result = _services.Documents.TryCloseDoc(doc.Id, allowNegative: false);
        if (result.Errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", result.Errors), "Проверка операции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Warnings.Count > 0)
        {
            var warningText = "Остаток уйдет в минус:\n" + string.Join("\n", result.Warnings) + "\n\nЗакрыть операцию?";
            var confirm = MessageBox.Show(warningText, "Предупреждение", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            result = _services.Documents.TryCloseDoc(doc.Id, allowNegative: true);
            if (!result.Success)
            {
                if (result.Errors.Count > 0)
                {
                    MessageBox.Show(string.Join("\n", result.Errors), "Проверка операции", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
        }

        if (!result.Success)
        {
            return;
        }

        LoadDocs();
        LoadStock(StatusSearchBox.Text);
        if (doc.Type == DocType.Outbound)
        {
            LoadOrders();
        }
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ItemNameBox.Text))
        {
            MessageBox.Show("Введите наименование товара.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var barcode = NormalizeIdentifier(ItemBarcodeBox.Text);
        var gtin = NormalizeIdentifier(ItemGtinBox.Text);
        if (!TryValidateItemIdentifiers(barcode, gtin, null))
        {
            return;
        }

        try
        {
            var baseUom = (ItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.CreateItem(ItemNameBox.Text, barcode, gtin, baseUom);
            LoadItems();
            ClearItemForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException ex) when (IsPostgresConstraint(ex))
        {
            if (TryShowItemBarcodeDuplicate(barcode, null))
            {
                return;
            }

            MessageBox.Show("Не удалось сохранить товар. Нарушено ограничение базы данных.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

        try
        {
            var summary = ImportItemsFromExcel(dialog.FileName);
            LoadItems();
            var message =
                "Импорт завершен.\n" +
                $"Создано: {summary.Created}\n" +
                $"Пропущено (дубликаты): {summary.Duplicates}\n" +
                $"Пропущено (пустые строки): {summary.EmptyRows}\n" +
                $"Пропущено (некорректные строки): {summary.InvalidRows}\n" +
                $"Ошибки: {summary.Errors}";
            MessageBox.Show(message, "Товары", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        ReloadItemsAndSelect(_selectedItem.Id);
    }

    private void ItemDisplayUomCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressPackagingSelection || _selectedItem == null)
        {
            return;
        }

        if (ItemDisplayUomCombo.SelectedItem is not PackagingOption option)
        {
            return;
        }

        try
        {
            _services.Packagings.SetDefaultPackaging(_selectedItem.Id, option.PackagingId);
            ReloadItemsAndSelect(_selectedItem.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
            LoadItemPackagingOptions(_selectedItem);
        }
    }

    private void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LocationCodeBox.Text) || string.IsNullOrWhiteSpace(LocationNameBox.Text))
        {
            MessageBox.Show("Введите код и наименование места хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Catalog.CreateLocation(LocationCodeBox.Text, LocationNameBox.Text);
            LoadLocations();
            ClearLocationForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddPartner_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PartnerNameBox.Text))
        {
            MessageBox.Show("Введите наименование контрагента.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var partnerCode = NormalizeIdentifier(PartnerCodeBox.Text);
        if (!TryValidatePartnerInn(partnerCode, null))
        {
            return;
        }

        try
        {
            var partnerId = _services.Catalog.CreatePartner(PartnerNameBox.Text, partnerCode);
            _services.PartnerStatuses.SetStatus(partnerId, GetSelectedPartnerStatus());
            LoadPartners();
            ClearPartnerForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException ex) when (IsPostgresConstraint(ex))
        {
            if (TryShowPartnerDuplicate(partnerCode, null))
            {
                return;
            }

            MessageBox.Show("Не удалось сохранить контрагента. Нарушено ограничение базы данных.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ItemsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedItem = ItemsGrid.SelectedItem as Item;
        ItemSaveButton.IsEnabled = _selectedItem != null;
        ItemDeleteButton.IsEnabled = _selectedItem != null;
        if (_selectedItem == null)
        {
            LoadItemPackagingOptions(null);
            return;
        }

        ItemNameBox.Text = _selectedItem.Name;
        ItemBarcodeBox.Text = _selectedItem.Barcode ?? string.Empty;
        ItemGtinBox.Text = _selectedItem.Gtin ?? string.Empty;
        ItemUomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, _selectedItem.BaseUom, StringComparison.OrdinalIgnoreCase));
        LoadItemPackagingOptions(_selectedItem);
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

    private void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null)
        {
            MessageBox.Show("Выберите товар.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var barcode = NormalizeIdentifier(ItemBarcodeBox.Text);
        var gtin = NormalizeIdentifier(ItemGtinBox.Text);
        if (!TryValidateItemIdentifiers(barcode, gtin, _selectedItem.Id))
        {
            return;
        }

        try
        {
            var baseUom = (ItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.UpdateItem(_selectedItem.Id, ItemNameBox.Text, barcode, gtin, baseUom);
            ReloadItemsAndSelect(_selectedItem.Id);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException ex) when (IsPostgresConstraint(ex))
        {
            if (TryShowItemBarcodeDuplicate(barcode, _selectedItem.Id))
            {
                return;
            }

            MessageBox.Show("Не удалось сохранить товар. Нарушено ограничение базы данных.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ImportItemsSummary ImportItemsFromExcel(string filePath)
    {
        EnsureExcelEncoding();

        var existingItems = _services.Catalog.GetItems(null);
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
                    _services.Catalog.CreateItem(name, code, gtin, null);
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

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
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
                    _services.Catalog.DeleteItem(item.Id);
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
        LocationSaveButton.IsEnabled = _selectedLocation != null;
        LocationDeleteButton.IsEnabled = _selectedLocation != null;
        if (_selectedLocation == null)
        {
            return;
        }

        LocationCodeBox.Text = _selectedLocation.Code;
        LocationNameBox.Text = _selectedLocation.Name;
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

    private void UpdateLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocation == null)
        {
            MessageBox.Show("Выберите место хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Catalog.UpdateLocation(_selectedLocation.Id, LocationCodeBox.Text, LocationNameBox.Text);
            LoadLocations();
            ClearLocationForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteLocation_Click(object sender, RoutedEventArgs e)
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
            _services.Catalog.DeleteLocation(_selectedLocation.Id);
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
        PartnerSaveButton.IsEnabled = _selectedPartner != null;
        PartnerDeleteButton.IsEnabled = _selectedPartner != null;
        if (_selectedPartner == null)
        {
            return;
        }

        PartnerNameBox.Text = _selectedPartner.Name;
        PartnerCodeBox.Text = _selectedPartner.Code ?? string.Empty;
        SetPartnerStatusSelection(_services.PartnerStatuses.GetStatus(_selectedPartner.Id));
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

    private bool IsItemFormEditing()
    {
        return ItemNameBox.IsKeyboardFocusWithin
               || ItemBarcodeBox.IsKeyboardFocusWithin
               || ItemGtinBox.IsKeyboardFocusWithin
               || ItemUomCombo.IsKeyboardFocusWithin
               || ItemDisplayUomCombo.IsKeyboardFocusWithin;
    }

    private bool IsLocationFormEditing()
    {
        return LocationCodeBox.IsKeyboardFocusWithin
               || LocationNameBox.IsKeyboardFocusWithin;
    }

    private bool IsPartnerFormEditing()
    {
        return PartnerNameBox.IsKeyboardFocusWithin
               || PartnerCodeBox.IsKeyboardFocusWithin
               || PartnerStatusCombo.IsKeyboardFocusWithin;
    }

    private void UpdatePartner_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartner == null)
        {
            MessageBox.Show("Выберите контрагента.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var partnerCode = NormalizeIdentifier(PartnerCodeBox.Text);
        if (!TryValidatePartnerInn(partnerCode, _selectedPartner.Id))
        {
            return;
        }

        try
        {
            _services.Catalog.UpdatePartner(_selectedPartner.Id, PartnerNameBox.Text, partnerCode);
            _services.PartnerStatuses.SetStatus(_selectedPartner.Id, GetSelectedPartnerStatus());
            LoadPartners();
            ClearPartnerForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException ex) when (IsPostgresConstraint(ex))
        {
            if (TryShowPartnerDuplicate(partnerCode, _selectedPartner.Id))
            {
                return;
            }

            MessageBox.Show("Не удалось сохранить контрагента. Нарушено ограничение базы данных.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeletePartner_Click(object sender, RoutedEventArgs e)
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
            _services.Catalog.DeletePartner(_selectedPartner.Id);
            _services.PartnerStatuses.RemoveStatus(_selectedPartner.Id);
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
                      ?? _services.Documents.GetDoc(window.CreatedDocId.Value);
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

        var result = _services.Import.ImportJsonl(path);
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
        if (!_services.AdminAuth.EnsureAdminPasswordExists())
        {
            var setWindow = new SetAdminPasswordWindow(_services.AdminAuth);
            setWindow.Owner = this;
            if (setWindow.ShowDialog() != true)
            {
                return;
            }
        }

        var prompt = new PasswordPromptWindow(_services.AdminAuth);
        prompt.Owner = this;
        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var window = new AdminWindow(_services, () =>
        {
            LoadDocs();
            LoadOrders();
            LoadStock(StatusSearchBox.Text);
            LoadItems();
            LoadLocations();
            LoadPartners();
            LoadUoms();
            ClearItemForm();
            ClearLocationForm();
            ClearPartnerForm();
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
    private void ClearItemForm()
    {
        _selectedItem = null;
        ItemNameBox.Text = string.Empty;
        ItemBarcodeBox.Text = string.Empty;
        ItemGtinBox.Text = string.Empty;
        ItemUomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, "шт", StringComparison.OrdinalIgnoreCase));
        ItemSaveButton.IsEnabled = false;
        ItemDeleteButton.IsEnabled = false;
        ItemsGrid.SelectedItem = null;
        LoadItemPackagingOptions(null);
    }

    private void ClearLocationForm()
    {
        _selectedLocation = null;
        LocationCodeBox.Text = string.Empty;
        LocationNameBox.Text = string.Empty;
        LocationSaveButton.IsEnabled = false;
        LocationDeleteButton.IsEnabled = false;
        LocationsGrid.SelectedItem = null;
    }

    private void ClearPartnerForm()
    {
        _selectedPartner = null;
        PartnerNameBox.Text = string.Empty;
        PartnerCodeBox.Text = string.Empty;
        SetPartnerStatusSelection(PartnerStatus.Both);
        PartnerSaveButton.IsEnabled = false;
        PartnerDeleteButton.IsEnabled = false;
        PartnersGrid.SelectedItem = null;
    }

    private PartnerStatus GetSelectedPartnerStatus()
    {
        return (PartnerStatusCombo.SelectedItem as PartnerStatusOption)?.Status ?? PartnerStatus.Both;
    }

    private void SetPartnerStatusSelection(PartnerStatus status)
    {
        PartnerStatusCombo.SelectedItem = _partnerStatusOptions.FirstOrDefault(option => option.Status == status)
                                          ?? _partnerStatusOptions.LastOrDefault();
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

    private void PartnerCodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsDigitsOnly(e.Text);
    }

    private void PartnerCodeBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(System.Windows.DataFormats.Text) as string;
        if (!IsDigitsOnly(text))
        {
            e.CancelCommand();
        }
    }

    private bool TryValidatePartnerInn(string? inn, long? currentPartnerId)
    {
        if (!string.IsNullOrWhiteSpace(inn) && !IsDigitsOnly(inn))
        {
            MessageBox.Show("ИНН должен содержать только цифры.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return !TryShowPartnerDuplicate(inn, currentPartnerId);
    }

    private bool TryShowPartnerDuplicate(string? inn, long? currentPartnerId)
    {
        var duplicate = FindPartnerByInn(inn, currentPartnerId);
        if (duplicate == null)
        {
            return false;
        }

        MessageBox.Show($"Контрагент с таким ИНН уже существует: {duplicate.Name}. Продолжить нельзя.",
            "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private Partner? FindPartnerByInn(string? inn, long? currentPartnerId)
    {
        if (string.IsNullOrWhiteSpace(inn))
        {
            return null;
        }

        var partner = _services.DataStore.FindPartnerByCode(inn);
        if (partner == null)
        {
            return null;
        }

        if (currentPartnerId.HasValue && partner.Id == currentPartnerId.Value)
        {
            return null;
        }

        return partner;
    }

    private bool TryValidateItemIdentifiers(string? barcode, string? gtin, long? currentItemId)
    {
        var items = _services.Catalog.GetItems(null);

        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var duplicate = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Barcode)
                                                         && string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase)
                                                         && (!currentItemId.HasValue || item.Id != currentItemId.Value));
            if (duplicate != null)
            {
                MessageBox.Show($"Товар с таким SKU / штрихкодом уже существует: {duplicate.Name}. Продолжить нельзя.",
                    "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(gtin))
        {
            var duplicate = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Gtin)
                                                         && string.Equals(item.Gtin, gtin, StringComparison.OrdinalIgnoreCase)
                                                         && (!currentItemId.HasValue || item.Id != currentItemId.Value));
            if (duplicate != null)
            {
                MessageBox.Show($"Товар с таким GTIN уже существует: {duplicate.Name}. Продолжить нельзя.",
                    "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private bool TryShowItemBarcodeDuplicate(string? barcode, long? currentItemId)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return false;
        }

        var duplicate = _services.Catalog.GetItems(null)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Barcode)
                                    && string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase)
                                    && (!currentItemId.HasValue || item.Id != currentItemId.Value));
        if (duplicate == null)
        {
            return false;
        }

        MessageBox.Show($"Товар с таким SKU / штрихкодом уже существует: {duplicate.Name}. Продолжить нельзя.",
            "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
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

    private sealed record PartnerStatusOption(PartnerStatus Status, string Name);

    private sealed record PartnerRow(Partner Partner, string StatusDisplay)
    {
        public long Id => Partner.Id;
        public string Name => Partner.Name;
        public string? Code => Partner.Code;
        public DateTime CreatedAt => Partner.CreatedAt;
    }

    private sealed record StockDisplayRow
    {
        public string ItemName { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public string LocationCode { get; init; } = string.Empty;
        public string HuDisplay { get; init; } = string.Empty;
        public string PackagingDisplay { get; init; } = string.Empty;
        public string BaseDisplay { get; init; } = string.Empty;
    }

    private sealed record StockLocationFilterOption(string? Code, string Name);

    private sealed record StockHuFilterOption(string? Code, string Name);

    private sealed record PackagingOption(long? PackagingId, string Name);

    private sealed record ImportItemsSummary(int Created, int Duplicates, int EmptyRows, int InvalidRows, int Errors);
}

