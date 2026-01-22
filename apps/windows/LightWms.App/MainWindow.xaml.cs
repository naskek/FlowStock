using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LightWms.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace LightWms.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Item> _items = new();
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Uom> _uoms = new();
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly ObservableCollection<Doc> _docs = new();
    private readonly ObservableCollection<Order> _orders = new();
    private readonly ObservableCollection<StockDisplayRow> _stock = new();
    private readonly ObservableCollection<PackagingOption> _itemPackagingOptions = new();
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
    private readonly DispatcherTimer _tsdTimer;
    private string? _lastTsdPath;
    private bool _lastTsdAvailable;
    private bool _tsdPromptVisible;
    private TsSyncWindow? _tsdWindow;
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
        DocsGrid.ItemsSource = _docs;
        OrdersGrid.ItemsSource = _orders;
        StockGrid.ItemsSource = _stock;
        DocsTypeFilter.ItemsSource = _docTypeFilters;
        DocsTypeFilter.SelectedIndex = 0;
        DocsStatusFilter.ItemsSource = _docStatusFilters;
        DocsStatusFilter.SelectedIndex = 0;

        LoadAll();
        ClearItemForm();
        ClearLocationForm();
        ClearPartnerForm();
        _tsdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _tsdTimer.Tick += TsdTimer_Tick;
        _tsdTimer.Start();
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

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            case Key.I:
                e.Handled = true;
                RunImportDialog();
                break;
            case Key.Enter:
                e.Handled = true;
                TryCloseSelectedDoc();
                break;
        }
    }

    private void LoadItems()
    {
        _items.Clear();
        foreach (var item in _services.Catalog.GetItems(null))
        {
            _items.Add(item);
        }
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
        _locations.Clear();
        foreach (var location in _services.Catalog.GetLocations())
        {
            _locations.Add(location);
        }
    }

    private void LoadPartners()
    {
        _partners.Clear();
        foreach (var partner in _services.Catalog.GetPartners())
        {
            _partners.Add(partner);
        }
    }

    private void LoadDocs()
    {
        _docs.Clear();
        foreach (var doc in ApplyDocFilters(_services.Documents.GetDocs()))
        {
            _docs.Add(doc);
        }
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
        _orders.Clear();
        foreach (var order in _services.Orders.GetOrders())
        {
            _orders.Add(order);
        }
    }

    private void LoadStock(string? search)
    {
        _stock.Clear();
        foreach (var row in _services.Documents.GetStock(search))
        {
            var packaging = _services.Packagings.FormatAsPackaging(row.ItemId, row.Qty);
            var baseDisplay = $"{FormatQty(row.Qty)} {row.BaseUom}";
            _stock.Add(new StockDisplayRow
            {
                ItemName = row.ItemName,
                Barcode = row.Barcode,
                LocationCode = row.LocationCode,
                PackagingDisplay = packaging,
                BaseDisplay = baseDisplay
            });
        }

        UpdateStockEmptyState(search);
    }

    private void UpdateStockEmptyState(string? search)
    {
        if (string.IsNullOrWhiteSpace(search) && _stock.Count == 0)
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

    private void StatusRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private void DocsRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadDocs();
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

    private void OrdersRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadOrders();
    }

    private void OrdersNew_Click(object sender, RoutedEventArgs e)
    {
        var window = new OrderDetailsWindow(_services);
        window.Owner = this;
        window.ShowDialog();
        LoadOrders();
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
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ItemNameBox.Text))
        {
            MessageBox.Show("Введите наименование товара.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var baseUom = (ItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.CreateItem(ItemNameBox.Text, ItemBarcodeBox.Text, ItemGtinBox.Text, baseUom);
            LoadItems();
            ClearItemForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            _services.Catalog.CreatePartner(PartnerNameBox.Text, partnerCode);
            LoadPartners();
            ClearPartnerForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (SqliteException ex) when (IsSqliteConstraint(ex))
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

    private void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null)
        {
            MessageBox.Show("Выберите товар.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var baseUom = (ItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.UpdateItem(_selectedItem.Id, ItemNameBox.Text, ItemBarcodeBox.Text, ItemGtinBox.Text, baseUom);
            ReloadItemsAndSelect(_selectedItem.Id);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null)
        {
            MessageBox.Show("Выберите товар.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранный товар?", "Товары", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Catalog.DeleteItem(_selectedItem.Id);
            LoadItems();
            ClearItemForm();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        _selectedPartner = PartnersGrid.SelectedItem as Partner;
        PartnerSaveButton.IsEnabled = _selectedPartner != null;
        PartnerDeleteButton.IsEnabled = _selectedPartner != null;
        if (_selectedPartner == null)
        {
            return;
        }

        PartnerNameBox.Text = _selectedPartner.Name;
        PartnerCodeBox.Text = _selectedPartner.Code ?? string.Empty;
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
            LoadPartners();
            ClearPartnerForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (SqliteException ex) when (IsSqliteConstraint(ex))
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
        MessageBox.Show(
            $"Импорт завершен.\nИмпортировано: {result.Imported}\nДубли: {result.Duplicates}\nОшибки: {result.Errors}",
            "Импорт",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

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

    private void ExportTsdData_Click(object sender, RoutedEventArgs e)
    {
        var exportDir = Path.Combine(_services.BaseDir, "Exports");
        Directory.CreateDirectory(exportDir);

        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = TsSyncWindow.TsdDataFileName,
            InitialDirectory = exportDir
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            TsSyncWindow.ExportTsdData(_services, dialog.FileName);
            MessageBox.Show($"Файл сохранен:\n{dialog.FileName}", "Выгрузка на ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error($"TSD export failed path={dialog.FileName}", ex);
            MessageBox.Show(ex.Message, "Выгрузка на ТСД", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenTsdSync_Click(object sender, RoutedEventArgs e)
    {
        OpenTsdSyncWindow();
    }

    private void OpenTsdSyncWindow()
    {
        if (_tsdWindow != null)
        {
            _tsdWindow.Activate();
            return;
        }

        var window = new TsSyncWindow(_services, () =>
        {
            LoadDocs();
            LoadStock(StatusSearchBox.Text);
        })
        {
            Owner = this
        };

        _tsdWindow = window;
        window.Closed += (_, _) => _tsdWindow = null;
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

    private void TsdTimer_Tick(object? sender, EventArgs e)
    {
        var settings = _services.Settings.Load();
        var path = settings.TsdFolderPath;

        if (!settings.TsdAutoPromptEnabled || string.IsNullOrWhiteSpace(path))
        {
            _lastTsdAvailable = false;
            _lastTsdPath = path;
            return;
        }

        path = path.Trim();
        if (!string.Equals(path, _lastTsdPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastTsdPath = path;
            _lastTsdAvailable = false;
        }

        var available = Directory.Exists(path);
        if (available && !_lastTsdAvailable && _tsdWindow == null && !_tsdPromptVisible)
        {
            ShowTsdPrompt();
        }

        _lastTsdAvailable = available;
    }

    private void ShowTsdPrompt()
    {
        _tsdPromptVisible = true;
        var prompt = new TsdPromptWindow
        {
            Owner = this
        };
        prompt.ShowDialog();
        _tsdPromptVisible = false;

        if (prompt.Choice == TsdPromptChoice.Open)
        {
            OpenTsdSyncWindow();
            return;
        }

        if (prompt.Choice == TsdPromptChoice.Disable)
        {
            var settings = _services.Settings.Load();
            settings.TsdAutoPromptEnabled = false;
            _services.Settings.Save(settings);
        }
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
        PartnerSaveButton.IsEnabled = false;
        PartnerDeleteButton.IsEnabled = false;
        PartnersGrid.SelectedItem = null;
    }

    private void PartnerCodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsDigitsOnly(e.Text);
    }

    private void PartnerCodeBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.Text) as string;
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

    private static bool IsSqliteConstraint(SqliteException ex)
    {
        return ex.SqliteErrorCode == (int)SqliteErrorCode.Constraint;
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private sealed record DocTypeFilterOption(DocType? Type, string Name);

    private sealed record DocStatusFilterOption(DocStatus? Status, string Name);

    private sealed record StockDisplayRow
    {
        public string ItemName { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public string LocationCode { get; init; } = string.Empty;
        public string PackagingDisplay { get; init; } = string.Empty;
        public string BaseDisplay { get; init; } = string.Empty;
    }

    private sealed record PackagingOption(long? PackagingId, string Name);
}
