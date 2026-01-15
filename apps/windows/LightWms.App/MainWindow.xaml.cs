using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LightWms.Core.Models;
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
    private readonly ObservableCollection<StockRow> _stock = new();
    private Item? _selectedItem;
    private Location? _selectedLocation;
    private Partner? _selectedPartner;
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
        PartnersGrid.ItemsSource = _partners;
        DocsGrid.ItemsSource = _docs;
        OrdersGrid.ItemsSource = _orders;
        StockGrid.ItemsSource = _stock;

        LoadAll();
        ClearItemForm();
        ClearLocationForm();
        ClearPartnerForm();
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
        foreach (var doc in _services.Documents.GetDocs())
        {
            _docs.Add(doc);
        }
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
            _stock.Add(row);
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
            var uom = (ItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.CreateItem(ItemNameBox.Text, ItemBarcodeBox.Text, ItemGtinBox.Text, uom);
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

        try
        {
            _services.Catalog.CreatePartner(PartnerNameBox.Text, PartnerCodeBox.Text);
            LoadPartners();
            ClearPartnerForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            return;
        }

        ItemNameBox.Text = _selectedItem.Name;
        ItemBarcodeBox.Text = _selectedItem.Barcode ?? string.Empty;
        ItemGtinBox.Text = _selectedItem.Gtin ?? string.Empty;
        ItemUomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, _selectedItem.Uom, StringComparison.OrdinalIgnoreCase));
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
            var uom = (ItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.UpdateItem(_selectedItem.Id, ItemNameBox.Text, ItemBarcodeBox.Text, ItemGtinBox.Text, uom);
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

        try
        {
            _services.Catalog.UpdatePartner(_selectedPartner.Id, PartnerNameBox.Text, PartnerCodeBox.Text);
            LoadPartners();
            ClearPartnerForm();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var dataDir = Path.GetDirectoryName(_services.DatabasePath);
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
        var dataDir = Path.GetDirectoryName(_services.DatabasePath);
        var logsDir = string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, "logs");
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
        ItemUomCombo.SelectedItem = null;
        ItemSaveButton.IsEnabled = false;
        ItemDeleteButton.IsEnabled = false;
        ItemsGrid.SelectedItem = null;
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
}
