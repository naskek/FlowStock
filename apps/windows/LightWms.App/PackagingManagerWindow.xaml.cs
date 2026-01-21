using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class PackagingManagerWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ItemOption> _items = new();
    private readonly ObservableCollection<ItemFilterOption> _filters = new();
    private readonly ObservableCollection<PackagingRow> _packagings = new();
    private PackagingRow? _selectedRow;

    public PackagingManagerWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ItemCombo.ItemsSource = _items;
        FilterItemCombo.ItemsSource = _filters;
        PackagingGrid.ItemsSource = _packagings;

        LoadItems();
        LoadPackagings();
        ClearForm();
    }

    private void LoadItems()
    {
        _items.Clear();
        _filters.Clear();
        _filters.Add(new ItemFilterOption(null, "Все товары"));

        foreach (var item in _services.Catalog.GetItems(null))
        {
            var option = new ItemOption(item.Id, item.Name, item.BaseUom);
            _items.Add(option);
            _filters.Add(new ItemFilterOption(option, option.DisplayName));
        }

        if (_filters.Count > 0)
        {
            FilterItemCombo.SelectedIndex = 0;
        }
    }

    private void LoadPackagings()
    {
        _packagings.Clear();
        var filterItem = (FilterItemCombo.SelectedItem as ItemFilterOption)?.Item;

        if (filterItem != null)
        {
            AddPackagingsForItem(filterItem);
        }
        else
        {
            foreach (var item in _items)
            {
                AddPackagingsForItem(item);
            }
        }

        _selectedRow = null;
        UpdateButtons();
    }

    private void AddPackagingsForItem(ItemOption item)
    {
        foreach (var packaging in _services.Packagings.GetPackagings(item.Id, includeInactive: true))
        {
            _packagings.Add(new PackagingRow
            {
                Id = packaging.Id,
                ItemId = item.Id,
                ItemDisplay = item.DisplayName,
                ItemBaseUom = item.BaseUom,
                Code = packaging.Code,
                Name = packaging.Name,
                FactorToBase = packaging.FactorToBase,
                IsActive = packaging.IsActive,
                SortOrder = packaging.SortOrder
            });
        }
    }

    private void FilterItemCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadPackagings();
    }

    private void FilterReset_Click(object sender, RoutedEventArgs e)
    {
        FilterItemCombo.SelectedIndex = 0;
    }

    private void PackagingGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedRow = PackagingGrid.SelectedItem as PackagingRow;
        if (_selectedRow == null)
        {
            ClearForm();
            UpdateButtons();
            return;
        }

        ItemCombo.SelectedItem = _items.FirstOrDefault(i => i.Id == _selectedRow.ItemId);
        PackagingCodeBox.Text = _selectedRow.Code;
        PackagingNameBox.Text = _selectedRow.Name;
        PackagingFactorBox.Text = _selectedRow.FactorToBase.ToString("0.###", CultureInfo.CurrentCulture);
        PackagingSortBox.Text = _selectedRow.SortOrder.ToString(CultureInfo.CurrentCulture);
        PackagingActiveCheck.IsChecked = _selectedRow.IsActive;
        UpdateButtons();
    }

    private void ItemCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemCombo.SelectedItem is ItemOption item)
        {
            PackagingFactorLabel.Text = $"Количество в упаковке ({item.BaseUom})";
            return;
        }

        PackagingFactorLabel.Text = "Количество в упаковке (база)";
    }

    private void AddPackaging_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var item, out var code, out var name, out var factor, out var sortOrder))
        {
            return;
        }

        try
        {
            _services.Packagings.CreatePackaging(item.Id, code, name, factor, sortOrder);
            LoadPackagings();
            SelectPackaging(item.Id, code);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SavePackaging_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow == null)
        {
            MessageBox.Show("Выберите упаковку.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadForm(out var item, out var code, out var name, out var factor, out var sortOrder))
        {
            return;
        }

        try
        {
            var isActive = PackagingActiveCheck.IsChecked == true;
            _services.Packagings.UpdatePackaging(_selectedRow.Id, item.Id, code, name, factor, sortOrder, isActive);
            LoadPackagings();
            SelectPackaging(item.Id, code);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeletePackaging_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow == null)
        {
            MessageBox.Show("Выберите упаковку.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранную упаковку?", "Упаковки", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Packagings.DeactivatePackaging(_selectedRow.Id);
            LoadPackagings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow == null)
        {
            MessageBox.Show("Выберите упаковку.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _services.Packagings.SetDefaultPackaging(_selectedRow.ItemId, _selectedRow.Id);
            MessageBox.Show("Упаковка по умолчанию установлена.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryReadForm(out ItemOption item, out string code, out string name, out double factor, out int sortOrder)
    {
        item = null!;
        code = PackagingCodeBox.Text.Trim();
        name = PackagingNameBox.Text.Trim();
        factor = 0;
        sortOrder = 0;

        if (ItemCombo.SelectedItem is not ItemOption selectedItem)
        {
            MessageBox.Show("Выберите товар.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("Введите код упаковки.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите наименование упаковки.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!double.TryParse(PackagingFactorBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out factor) || factor <= 0)
        {
            MessageBox.Show("Введите корректное количество в упаковке.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(PackagingSortBox.Text)
            && (!int.TryParse(PackagingSortBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out sortOrder) || sortOrder < 0))
        {
            MessageBox.Show("Введите корректный порядок сортировки.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        item = selectedItem;
        return true;
    }

    private void ClearForm()
    {
        ItemCombo.SelectedItem = null;
        PackagingCodeBox.Text = string.Empty;
        PackagingNameBox.Text = string.Empty;
        PackagingFactorBox.Text = string.Empty;
        PackagingSortBox.Text = "0";
        PackagingActiveCheck.IsChecked = true;
        PackagingFactorLabel.Text = "Количество в упаковке (база)";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasSelection = _selectedRow != null;
        SavePackagingButton.IsEnabled = hasSelection;
        DeletePackagingButton.IsEnabled = hasSelection;
        SetDefaultButton.IsEnabled = hasSelection && _selectedRow?.IsActive == true;
    }

    private void SelectPackaging(long itemId, string code)
    {
        var row = _packagings.FirstOrDefault(p => p.ItemId == itemId && string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
        if (row == null)
        {
            return;
        }

        PackagingGrid.SelectedItem = row;
        PackagingGrid.ScrollIntoView(row);
    }

    private sealed record ItemOption(long Id, string Name, string BaseUom)
    {
        public string DisplayName => $"{Name} ({BaseUom})";
    }

    private sealed record ItemFilterOption(ItemOption? Item, string DisplayName);

    private sealed record PackagingRow
    {
        public long Id { get; init; }
        public long ItemId { get; init; }
        public string ItemDisplay { get; init; } = string.Empty;
        public string ItemBaseUom { get; init; } = "шт";
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public double FactorToBase { get; init; }
        public bool IsActive { get; init; }
        public int SortOrder { get; init; }
    }
}
