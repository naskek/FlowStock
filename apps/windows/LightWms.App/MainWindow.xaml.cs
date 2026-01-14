using System.Collections.ObjectModel;
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

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        LocationsGrid.ItemsSource = _locations;
        DocsGrid.ItemsSource = _docs;
        DocLinesGrid.ItemsSource = _docLines;
        StockGrid.ItemsSource = _stock;

        LoadAll();
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
    }

    private void LoadStock(string? search)
    {
        _stock.Clear();
        foreach (var row in _services.Documents.GetStock(search))
        {
            _stock.Add(row);
        }
    }

    private void StatusSearch_Click(object sender, RoutedEventArgs e)
    {
        LoadStock(StatusSearchBox.Text);
    }

    private void StatusRefresh_Click(object sender, RoutedEventArgs e)
    {
        StatusSearchBox.Text = string.Empty;
        LoadStock(null);
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
        DocInfoText.Text = $"{doc.DocRef} | {DocTypeMapper.ToOpString(doc.Type)} | {DocTypeMapper.StatusToString(doc.Status)}";
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

        _services.Documents.CloseDoc(_selectedDoc.Id);
        LoadDocs();
        LoadStock(StatusSearchBox.Text);

        var refreshed = _docs.FirstOrDefault(d => d.Id == _selectedDoc.Id);
        if (refreshed != null)
        {
            _selectedDoc = refreshed;
            DocInfoText.Text = $"{refreshed.DocRef} | {DocTypeMapper.ToOpString(refreshed.Type)} | {DocTypeMapper.StatusToString(refreshed.Status)}";
        }

        LoadDocLines(_selectedDoc.Id);
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
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
        ImportStatsText.Text = $"Статистика: imported={result.Imported}, duplicates={result.Duplicates}, errors={result.Errors}";

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
}
