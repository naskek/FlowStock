using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class ItemPickerWindow : Window
{
    private readonly AppServices _services;
    private readonly IReadOnlyDictionary<long, double>? _availabilityByItem;
    private readonly ObservableCollection<ItemPickerRow> _items = new();
    private readonly ObservableCollection<FilterOption> _brandFilters = new();
    private readonly ObservableCollection<FilterOption> _volumeFilters = new();
    private readonly ObservableCollection<FilterOption> _uomFilters = new();
    private readonly ICollectionView _view;

    public Item? SelectedItem { get; private set; }
    public bool KeepOpenOnSelect { get; set; }
    public event EventHandler<Item>? ItemPicked;

    public ItemPickerWindow(
        AppServices services,
        IEnumerable<Item>? items = null,
        IReadOnlyDictionary<long, double>? availabilityByItem = null)
    {
        _services = services;
        _availabilityByItem = availabilityByItem;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        AvailableQtyColumn.Visibility = availabilityByItem == null ? Visibility.Collapsed : Visibility.Visible;
        LoadItems(items);

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;
        BrandFilterList.ItemsSource = _brandFilters;
        VolumeFilterList.ItemsSource = _volumeFilters;
        UomFilterList.ItemsSource = _uomFilters;
        BuildFilters();
        UpdateEmptyState();

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    private void LoadItems(IEnumerable<Item>? items)
    {
        _items.Clear();
        var source = items
            ?? (_services.WpfReadApi.TryGetItems(null, out var apiItems)
                ? apiItems
                : Array.Empty<Item>());
        foreach (var item in source)
        {
            var availableQty = _availabilityByItem != null && _availabilityByItem.TryGetValue(item.Id, out var qty)
                ? qty
                : (double?)null;
            _items.Add(new ItemPickerRow(item, availableQty));
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private bool FilterItem(object? obj)
    {
        if (obj is not ItemPickerRow row)
        {
            return false;
        }

        if (!MatchesFilter(row.Brand, _brandFilters))
        {
            return false;
        }

        if (!MatchesFilter(row.Volume, _volumeFilters))
        {
            return false;
        }

        if (!MatchesFilter(row.BaseUom, _uomFilters))
        {
            return false;
        }

        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Contains(row.Name, query)
               || Contains(row.Barcode, query)
               || Contains(row.Gtin, query);
    }

    private static bool Contains(string? source, string query)
    {
        return !string.IsNullOrWhiteSpace(source)
               && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CommitSelection();
    }

    private void ItemsGrid_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitSelection();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            return;
        }

        if (e.Key == Key.Enter && ItemsGrid.SelectedItem is ItemPickerRow)
        {
            e.Handled = true;
            CommitSelection();
        }
    }

    private void CommitSelection()
    {
        if (ItemsGrid.SelectedItem is not ItemPickerRow row)
        {
            return;
        }

        var item = row.Item;
        SelectedItem = item;
        ItemPicked?.Invoke(this, item);
        if (KeepOpenOnSelect)
        {
            return;
        }
        DialogResult = true;
        Close();
    }

    private void UpdateEmptyState()
    {
        if (_view.IsEmpty)
        {
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
    }

    private void BuildFilters()
    {
        BuildFilterOptions(_brandFilters, _items.Select(item => item.Brand));
        BuildFilterOptions(_volumeFilters, _items.Select(item => item.Volume));
        BuildFilterOptions(_uomFilters, _items.Select(item => item.BaseUom));
        ApplyFilters();
    }

    private void BuildFilterOptions(ObservableCollection<FilterOption> target, IEnumerable<string?> values)
    {
        foreach (var option in target)
        {
            option.PropertyChanged -= FilterOptionChanged;
        }

        target.Clear();

        var hasEmpty = false;
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = NormalizeFilterValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                hasEmpty = true;
                continue;
            }
            set.Add(normalized);
        }

        foreach (var value in set)
        {
            var option = new FilterOption(value, value);
            option.PropertyChanged += FilterOptionChanged;
            target.Add(option);
        }

        if (hasEmpty)
        {
            var emptyOption = new FilterOption("(пусто)", null);
            emptyOption.PropertyChanged += FilterOptionChanged;
            target.Add(emptyOption);
        }
    }

    private void FilterOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterOption.IsChecked))
        {
            ApplyFilters();
        }
    }

    private void ApplyFilters()
    {
        _view.Refresh();
        UpdateEmptyState();
    }

    private static string? NormalizeFilterValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool MatchesFilter(string? value, ObservableCollection<FilterOption> options)
    {
        if (options.Count == 0)
        {
            return true;
        }

        var anyUnchecked = options.Any(option => !option.IsChecked);
        if (!anyUnchecked)
        {
            return true;
        }

        var normalized = NormalizeFilterValue(value);
        return options.Any(option => option.IsChecked && option.Matches(normalized));
    }

    private static void SetAllFilters(ObservableCollection<FilterOption> options, bool isChecked)
    {
        foreach (var option in options)
        {
            option.IsChecked = isChecked;
        }
    }

    private void BrandFilterAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllFilters(_brandFilters, true);
    }

    private void BrandFilterNone_Click(object sender, RoutedEventArgs e)
    {
        SetAllFilters(_brandFilters, false);
    }

    private void VolumeFilterAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllFilters(_volumeFilters, true);
    }

    private void VolumeFilterNone_Click(object sender, RoutedEventArgs e)
    {
        SetAllFilters(_volumeFilters, false);
    }

    private void UomFilterAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllFilters(_uomFilters, true);
    }

    private void UomFilterNone_Click(object sender, RoutedEventArgs e)
    {
        SetAllFilters(_uomFilters, false);
    }

    private sealed class FilterOption : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public string Label { get; }
        public string? Value { get; }
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public FilterOption(string label, string? value)
        {
            Label = label;
            Value = value;
        }

        public bool Matches(string? candidate)
        {
            if (Value == null)
            {
                return string.IsNullOrWhiteSpace(candidate);
            }

            return string.Equals(Value, candidate, StringComparison.OrdinalIgnoreCase);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ItemPickerRow
    {
        public ItemPickerRow(Item item, double? availableQty)
        {
            Item = item;
            AvailableQty = availableQty;
        }

        public Item Item { get; }
        public string Name => Item.Name;
        public string? Brand => Item.Brand;
        public string? Volume => Item.Volume;
        public string? Barcode => Item.Barcode;
        public string? Gtin => Item.Gtin;
        public string BaseUom => Item.BaseUom;
        public double? AvailableQty { get; }
        public string AvailableQtyDisplay => AvailableQty.HasValue
            ? $"{AvailableQty.Value.ToString("0.###", CultureInfo.CurrentCulture)} {BaseUom}"
            : string.Empty;
    }
}

