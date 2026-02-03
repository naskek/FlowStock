using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class ImportErrorsWindow : Window
{
    private readonly AppServices _services;
    private readonly Action? _onDataChanged;
    private readonly ObservableCollection<ImportErrorView> _errors = new();
    private readonly ObservableCollection<Item> _items = new();
    private readonly ObservableCollection<Uom> _uoms = new();
    private ImportErrorView? _selectedError;

    public ImportErrorsWindow(AppServices services, Action? onDataChanged)
    {
        _services = services;
        _onDataChanged = onDataChanged;
        InitializeComponent();

        ErrorsGrid.ItemsSource = _errors;
        ItemsComboBox.ItemsSource = _items;
        NewItemUomCombo.ItemsSource = _uoms;

        LoadErrors();
        LoadItems();
        LoadUoms();
    }

    private void LoadErrors()
    {
        _errors.Clear();
        foreach (var error in _services.Import.GetImportErrors(null))
        {
            _errors.Add(error);
        }

        _selectedError = _errors.FirstOrDefault();
        ErrorsGrid.SelectedItem = _selectedError;
        UpdateSelection();
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

    private void UpdateSelection()
    {
        SelectedBarcodeText.Text = _selectedError?.Barcode ?? string.Empty;
    }

    private void ErrorsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedError = ErrorsGrid.SelectedItem as ImportErrorView;
        UpdateSelection();
    }

    private void BindBarcode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedError == null || string.IsNullOrWhiteSpace(_selectedError.Barcode))
        {
            MessageBox.Show("Выберите ошибку со штрихкодом.", "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ItemsComboBox.SelectedItem is not Item item)
        {
            MessageBox.Show("Выберите товар.", "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.Barcode) && item.Barcode != _selectedError.Barcode)
        {
            MessageBox.Show("У выбранного товара уже есть другой штрихкод.", "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _services.Catalog.AssignBarcode(item.Id, _selectedError.Barcode);
            LoadItems();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedError == null || string.IsNullOrWhiteSpace(_selectedError.Barcode))
        {
            MessageBox.Show("Выберите ошибку со штрихкодом.", "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var uom = (NewItemUomCombo.SelectedItem as Uom)?.Name;
            _services.Catalog.CreateItem(NewItemNameBox.Text, _selectedError.Barcode, NewItemGtinBox.Text, uom);
            NewItemNameBox.Text = string.Empty;
            NewItemGtinBox.Text = string.Empty;
            NewItemUomCombo.SelectedItem = null;
            LoadItems();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Reapply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedError == null)
        {
            MessageBox.Show("Выберите ошибку.", "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var applied = _services.Import.ReapplyError(_selectedError.Id);
        if (!applied)
        {
            MessageBox.Show("Не удалось переприменить. Проверьте, что штрихкод привязан к товару.", "Ошибки импорта", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadErrors();
        _onDataChanged?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadErrors();
        LoadItems();
        LoadUoms();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

