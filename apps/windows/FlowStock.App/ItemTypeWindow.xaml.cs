using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public partial class ItemTypeWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ItemType> _itemTypes = new();
    private readonly Action? _onChanged;
    private ItemType? _selectedItemType;

    public ItemTypeWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        ItemTypesGrid.ItemsSource = _itemTypes;
        LoadItemTypes();
        ResetForm();
    }

    private void LoadItemTypes()
    {
        _itemTypes.Clear();
        var itemTypes = _services.WpfCatalogApi.TryGetItemTypes(includeInactive: true, out var apiItemTypes)
            ? apiItemTypes
            : Array.Empty<ItemType>();
        foreach (var itemType in itemTypes)
        {
            _itemTypes.Add(itemType);
        }

        UpdateDeleteButton();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var code = string.IsNullOrWhiteSpace(CodeBox.Text) ? null : CodeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите название типа номенклатуры.", "Типы номенклатуры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse((SortOrderBox.Text ?? "0").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortOrder))
        {
            MessageBox.Show("Сортировка должна быть целым числом.", "Типы номенклатуры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var model = new ItemType
        {
            Id = _selectedItemType?.Id ?? 0,
            Name = name,
            Code = code,
            SortOrder = sortOrder,
            IsActive = IsActiveCheck.IsChecked == true,
            IsVisibleInProductCatalog = VisibleInCatalogCheck.IsChecked == true,
            EnableMinStockControl = EnableMinStockCheck.IsChecked == true
        };

        try
        {
            if (_selectedItemType == null)
            {
                var result = await _services.WpfCatalogApi.TryCreateItemTypeAsync(model).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось создать тип номенклатуры.");
                }
            }
            else
            {
                var result = await _services.WpfCatalogApi.TryUpdateItemTypeAsync(model).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось обновить тип номенклатуры.");
                }
            }

            LoadItemTypes();
            ResetForm();
            _onChanged?.Invoke();
        }
        catch (PostgresException)
        {
            MessageBox.Show("Тип номенклатуры с таким именем или кодом уже существует.", "Типы номенклатуры", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Типы номенклатуры", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemType == null)
        {
            MessageBox.Show("Выберите тип номенклатуры.", "Типы номенклатуры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить тип номенклатуры \"{_selectedItemType.Name}\"?\nЕсли тип используется в товарах, он будет деактивирован.",
            "Типы номенклатуры",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryDeleteItemTypeAsync(_selectedItemType.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось удалить тип номенклатуры.");
            }

            LoadItemTypes();
            ResetForm();
            _onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Типы номенклатуры", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
    }

    private void ItemTypesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedItemType = ItemTypesGrid.SelectedItem as ItemType;
        if (_selectedItemType == null)
        {
            ResetForm();
            return;
        }

        NameBox.Text = _selectedItemType.Name;
        CodeBox.Text = _selectedItemType.Code ?? string.Empty;
        SortOrderBox.Text = _selectedItemType.SortOrder.ToString(CultureInfo.InvariantCulture);
        IsActiveCheck.IsChecked = _selectedItemType.IsActive;
        VisibleInCatalogCheck.IsChecked = _selectedItemType.IsVisibleInProductCatalog;
        EnableMinStockCheck.IsChecked = _selectedItemType.EnableMinStockControl;
        SaveButton.Content = "Сохранить";
        UpdateDeleteButton();
    }

    private void ResetForm()
    {
        _selectedItemType = null;
        ItemTypesGrid.SelectedItem = null;
        NameBox.Text = string.Empty;
        CodeBox.Text = string.Empty;
        SortOrderBox.Text = "0";
        IsActiveCheck.IsChecked = true;
        VisibleInCatalogCheck.IsChecked = false;
        EnableMinStockCheck.IsChecked = false;
        SaveButton.Content = "Добавить";
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        if (DeleteButton != null)
        {
            DeleteButton.IsEnabled = _selectedItemType != null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
