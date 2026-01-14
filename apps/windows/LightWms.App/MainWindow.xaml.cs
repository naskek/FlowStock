using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using LightWms.Core.Models;
using Microsoft.Win32;

namespace LightWms.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Item> _items = new();
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Doc> _docs = new();
    private readonly ObservableCollection<DocLineView> _docLines = new();
    private readonly ObservableCollection<StockRow> _stock = new();
    private Doc? _selectedDoc;
    private DocLineView? _selectedDocLine;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        LocationsGrid.ItemsSource = _locations;
        DocsGrid.ItemsSource = _docs;
        DocLinesGrid.ItemsSource = _docLines;
        StockGrid.ItemsSource = _stock;
        DocItemCombo.ItemsSource = _items;
        DocFromCombo.ItemsSource = _locations;
        DocToCombo.ItemsSource = _locations;

        LoadAll();
        UpdateDocView();
    }

    private void LoadAll()
    {
        LoadItems();
        LoadLocations();
        LoadDocs();
        LoadStock(null);
    }

    private void LoadItems()
    {
        _items.Clear();
        foreach (var item in _services.Catalog.GetItems(null))
        {
            _items.Add(item);
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
        if (DocsGrid.SelectedItem is not Doc doc)
        {
            MessageBox.Show("Выберите документ.", "Документы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedDoc = doc;
        UpdateDocView();
        LoadDocLines(doc.Id);
        MainTabs.SelectedIndex = 2;
    }

    private void DocClose_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show("Введите имя товара.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Catalog.CreateItem(ItemNameBox.Text, ItemBarcodeBox.Text, ItemGtinBox.Text, ItemUomBox.Text);
            ItemNameBox.Text = string.Empty;
            ItemBarcodeBox.Text = string.Empty;
            ItemGtinBox.Text = string.Empty;
            ItemUomBox.Text = string.Empty;
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
            MessageBox.Show("Введите код и имя локации.", "Локации", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(ex.Message, "Локации", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Локации", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSONL files (*.jsonl)|*.jsonl|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            ImportFilePathBox.Text = dialog.FileName;
        }
    }

    private void ImportExecute_Click(object sender, RoutedEventArgs e)
    {
        var path = ImportFilePathBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("Файл не найден.", "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = _services.Import.ImportJsonl(path);
        ImportStatsText.Text = $"Статистика: импортировано={result.Imported}, дубли={result.Duplicates}, ошибки={result.Errors}";

        LoadDocs();
    }

    private void ImportErrors_Click(object sender, RoutedEventArgs e)
    {
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

    private void DocAddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (DocItemCombo.SelectedItem is not Item item)
        {
            MessageBox.Show("Выберите товар.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        else if (_selectedDoc.Type == DocType.WriteOff)
        {
            toLocation = null;
        }

        if (!ValidateLineLocations(_selectedDoc!, fromLocation, toLocation))
        {
            return;
        }

        try
        {
            _services.Documents.AddDocLine(_selectedDoc!.Id, item.Id, qty, fromLocation?.Id, toLocation?.Id);
            DocItemQtyBox.Text = string.Empty;
            LoadDocLines(_selectedDoc.Id);
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

    private void UpdateDocView()
    {
        if (_selectedDoc == null)
        {
            DocInfoText.Text = string.Empty;
            DocCloseButton.IsEnabled = false;
            DocLinesGrid.Visibility = Visibility.Collapsed;
            DocEmptyText.Visibility = Visibility.Visible;
            DocEditGroup.Visibility = Visibility.Collapsed;
            return;
        }

        DocInfoText.Text = FormatDocHeader(_selectedDoc);
        DocCloseButton.IsEnabled = _selectedDoc.Status != DocStatus.Closed;
        DocLinesGrid.Visibility = Visibility.Visible;
        DocEmptyText.Visibility = Visibility.Collapsed;
        DocEditGroup.Visibility = _selectedDoc.Status == DocStatus.Draft ? Visibility.Visible : Visibility.Collapsed;

        var showFrom = _selectedDoc.Type != DocType.Inbound;
        var showTo = _selectedDoc.Type != DocType.WriteOff;
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
    }

    private static string FormatDocHeader(Doc doc)
    {
        var createdAt = doc.CreatedAt.ToString("g");
        var closedAt = doc.ClosedAt.HasValue ? doc.ClosedAt.Value.ToString("g") : "—";
        return $"Ref: {doc.DocRef} | Type: {DocTypeMapper.ToOpString(doc.Type)} | Status: {DocTypeMapper.StatusToString(doc.Status)} | CreatedAt: {createdAt} | ClosedAt: {closedAt}";
    }

    private static bool TryParseQty(string input, out double qty)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out qty) && qty > 0;
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
                    MessageBox.Show("Для приемки выберите локацию получателя (to).", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.WriteOff:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для списания выберите локацию источника (from).", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Move:
                if (fromLocation == null || toLocation == null)
                {
                    MessageBox.Show("Для перемещения выберите обе локации.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (fromLocation.Id == toLocation.Id)
                {
                    MessageBox.Show("Для перемещения локации должны быть разными.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            default:
                return true;
        }
    }
}
