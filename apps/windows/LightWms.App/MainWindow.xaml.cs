using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
    private readonly ObservableCollection<DocLineView> _docLines = new();
    private readonly ObservableCollection<StockRow> _stock = new();
    private Doc? _selectedDoc;
    private DocLineView? _selectedDocLine;
    private const int TabStatusIndex = 0;
    private const int TabDocsIndex = 1;
    private const int TabDocIndex = 2;
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
        DocLinesGrid.ItemsSource = _docLines;
        StockGrid.ItemsSource = _stock;
        DocItemCombo.ItemsSource = _items;
        DocFromCombo.ItemsSource = _locations;
        DocToCombo.ItemsSource = _locations;
        DocPartnerCombo.ItemsSource = _partners;

        LoadAll();
        UpdateDocView();
    }

    private void LoadAll()
    {
        LoadItems();
        LoadUoms();
        LoadLocations();
        LoadPartners();
        LoadDocs();
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
                TryCloseCurrentDoc();
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

    private void LoadDocLines(long docId)
    {
        _docLines.Clear();
        foreach (var line in _services.Documents.GetDocLines(docId))
        {
            _docLines.Add(line);
        }

        _selectedDocLine = null;
        DocLineQtyBox.Text = string.Empty;
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
            MessageBox.Show("Выберите документ.", "Операции", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenDoc(doc);
    }

    private void OpenDoc(Doc doc)
    {
        _selectedDoc = doc;
        UpdateDocView();
        LoadDocLines(doc.Id);
        MainTabs.SelectedIndex = TabDocIndex;
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

    private void DocClose_Click(object sender, RoutedEventArgs e)
    {
        TryCloseCurrentDoc();
    }

    private void TryCloseCurrentDoc()
    {
        if (_selectedDoc == null)
        {
            MessageBox.Show("Документ не выбран.", "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedDoc.Status == DocStatus.Closed)
        {
            MessageBox.Show("Документ уже закрыт.", "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = _services.Documents.TryCloseDoc(_selectedDoc.Id, allowNegative: false);
        if (result.Errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", result.Errors), "Проверка документа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Warnings.Count > 0)
        {
            var warningText = "Остаток уйдет в минус:\n" + string.Join("\n", result.Warnings) + "\n\nЗакрыть документ?";
            var confirm = MessageBox.Show(warningText, "Предупреждение", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            result = _services.Documents.TryCloseDoc(_selectedDoc.Id, allowNegative: true);
            if (!result.Success)
            {
                if (result.Errors.Count > 0)
                {
                    MessageBox.Show(string.Join("\n", result.Errors), "Проверка документа", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var refreshed = _docs.FirstOrDefault(d => d.Id == _selectedDoc.Id);
        _selectedDoc = refreshed;
        UpdateDocView();

        if (_selectedDoc != null)
        {
            LoadDocLines(_selectedDoc.Id);
        }
        else
        {
            _docLines.Clear();
        }
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
            ItemNameBox.Text = string.Empty;
            ItemBarcodeBox.Text = string.Empty;
            ItemGtinBox.Text = string.Empty;
            ItemUomCombo.SelectedItem = null;
            LoadItems();
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
            LocationCodeBox.Text = string.Empty;
            LocationNameBox.Text = string.Empty;
            LoadLocations();
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
            PartnerNameBox.Text = string.Empty;
            PartnerCodeBox.Text = string.Empty;
            LoadPartners();
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
            OpenDoc(created);
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

    private void ViewDoc_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(TabDocIndex);
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

    private void DocLines_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedDocLine = DocLinesGrid.SelectedItem as DocLineView;
        if (_selectedDocLine == null)
        {
            DocLineQtyBox.Text = string.Empty;
            return;
        }

        DocLineQtyBox.Text = _selectedDocLine.Qty.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private void DocLinesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            DocDeleteLine_Click(sender, new RoutedEventArgs());
        }
    }

    private void DocBarcodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DocAddLine_Click(sender, new RoutedEventArgs());
        }
    }

    private void DocAddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (!TryResolveDocItem(out var item))
        {
            return;
        }

        if (!TryParseQty(DocItemQtyBox.Text, out var qty))
        {
            MessageBox.Show("Количество должно быть больше 0.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fromLocation = DocFromCombo.SelectedItem as Location;
        var toLocation = DocToCombo.SelectedItem as Location;
        if (_selectedDoc!.Type == DocType.Inbound)
        {
            fromLocation = null;
        }
        else if (_selectedDoc.Type == DocType.WriteOff || _selectedDoc.Type == DocType.Outbound)
        {
            toLocation = null;
        }

        if (!ValidateLineLocations(_selectedDoc!, fromLocation, toLocation))
        {
            return;
        }

        try
        {
            _services.Documents.AddDocLine(_selectedDoc!.Id, item!.Id, qty, fromLocation?.Id, toLocation?.Id);
            DocItemQtyBox.Text = string.Empty;
            DocBarcodeBox.Text = string.Empty;
            LoadDocLines(_selectedDoc.Id);
            DocBarcodeBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocUpdateLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseQty(DocLineQtyBox.Text, out var qty))
        {
            MessageBox.Show("Количество должно быть больше 0.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Documents.UpdateDocLineQty(_selectedDoc!.Id, _selectedDocLine.Id, qty);
            LoadDocLines(_selectedDoc.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocDeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Documents.DeleteDocLine(_selectedDoc!.Id, _selectedDocLine.Id);
            LoadDocLines(_selectedDoc.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocHeaderSave_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDoc == null)
        {
            MessageBox.Show("Документ не выбран.", "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedDoc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Документ уже закрыт.", "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var partnerId = (DocPartnerCombo.SelectedItem as Partner)?.Id;
        try
        {
            _services.Documents.UpdateDocHeader(_selectedDoc.Id, partnerId, DocOrderRefBox.Text, DocShippingRefBox.Text);
            LoadDocs();
            var refreshed = _services.Documents.GetDoc(_selectedDoc.Id);
            _selectedDoc = refreshed ?? _selectedDoc;
            UpdateDocView();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDocView()
    {
        if (_selectedDoc == null)
        {
            DocInfoText.Text = string.Empty;
            DocCloseButton.IsEnabled = false;
            DocLinesGrid.Visibility = Visibility.Collapsed;
            DocEmptyText.Visibility = Visibility.Visible;
            DocEditGroup.Visibility = Visibility.Collapsed;
            DocHeaderPanel.IsEnabled = false;
            DocHeaderPanel.Visibility = Visibility.Collapsed;
            DocPartnerCombo.SelectedItem = null;
            DocOrderRefBox.Text = string.Empty;
            DocShippingRefBox.Text = string.Empty;
            return;
        }

        DocInfoText.Text = FormatDocHeader(_selectedDoc);
        DocCloseButton.IsEnabled = _selectedDoc.Status != DocStatus.Closed;
        DocLinesGrid.Visibility = Visibility.Visible;
        DocEmptyText.Visibility = Visibility.Collapsed;
        DocEditGroup.Visibility = _selectedDoc.Status == DocStatus.Draft ? Visibility.Visible : Visibility.Collapsed;
        DocHeaderPanel.Visibility = Visibility.Visible;

        var showFrom = _selectedDoc.Type != DocType.Inbound;
        var showTo = _selectedDoc.Type != DocType.WriteOff && _selectedDoc.Type != DocType.Outbound;
        DocFromLabel.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocFromCombo.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocToLabel.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;
        DocToCombo.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;

        if (!showFrom)
        {
            DocFromCombo.SelectedItem = null;
        }

        if (!showTo)
        {
            DocToCombo.SelectedItem = null;
        }

        DocHeaderPanel.IsEnabled = _selectedDoc.Status == DocStatus.Draft;
        DocPartnerCombo.SelectedItem = _partners.FirstOrDefault(p => p.Id == _selectedDoc.PartnerId);
        DocOrderRefBox.Text = _selectedDoc.OrderRef ?? string.Empty;
        DocShippingRefBox.Text = _selectedDoc.ShippingRef ?? string.Empty;

        if (_selectedDoc.Status == DocStatus.Draft)
        {
            DocBarcodeBox.Focus();
            DocBarcodeBox.SelectAll();
        }
    }

    private static string FormatDocHeader(Doc doc)
    {
        var createdAt = doc.CreatedAt.ToString("g");
        var closedAt = doc.ClosedAt.HasValue ? doc.ClosedAt.Value.ToString("g") : "—";
        return $"Номер: {doc.DocRef} | Тип: {DocTypeMapper.ToDisplayName(doc.Type)} | Статус: {DocTypeMapper.StatusToDisplayName(doc.Status)} | Создан: {createdAt} | Закрыт: {closedAt}";
    }

    private static bool TryParseQty(string input, out double qty)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out qty) && qty > 0;
    }

    private bool TryResolveDocItem(out Item? item)
    {
        item = null;
        var barcode = DocBarcodeBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            item = _items.FirstOrDefault(i => string.Equals(i.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
                   ?? _items.FirstOrDefault(i => string.Equals(i.Gtin, barcode, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                MessageBox.Show("Товар со штрихкодом/GTIN не найден.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DocItemCombo.SelectedItem = item;
            return true;
        }

        if (DocItemCombo.SelectedItem is Item selected)
        {
            item = selected;
            return true;
        }

        MessageBox.Show("Выберите товар или укажите штрихкод.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool EnsureDraftDocSelected()
    {
        if (_selectedDoc == null)
        {
            MessageBox.Show("Документ не выбран.", "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_selectedDoc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Документ уже закрыт.", "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private bool ValidateLineLocations(Doc doc, Location? fromLocation, Location? toLocation)
    {
        switch (doc.Type)
        {
            case DocType.Inbound:
                if (toLocation == null)
                {
                    MessageBox.Show("Для приемки выберите место хранения получателя.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.WriteOff:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для списания выберите место хранения источника.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Outbound:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для отгрузки выберите место хранения источника.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Move:
                if (fromLocation == null || toLocation == null)
                {
                    MessageBox.Show("Для перемещения выберите места хранения откуда/куда.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (fromLocation.Id == toLocation.Id)
                {
                    MessageBox.Show("Для перемещения места хранения откуда/куда должны быть разными.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            default:
                return true;
        }
    }
}
