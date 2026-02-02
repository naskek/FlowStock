using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class ItemPickerWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Item> _items = new();
    private readonly ICollectionView _view;

    public Item? SelectedItem { get; private set; }

    public ItemPickerWindow(AppServices services, IEnumerable<Item>? items = null)
    {
        _services = services;
        InitializeComponent();

        ItemsGrid.ItemsSource = _items;
        LoadItems(items);

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;
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
        var source = items ?? _services.Catalog.GetItems(null);
        foreach (var item in source)
        {
            _items.Add(item);
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateEmptyState();
    }

    private bool FilterItem(object? obj)
    {
        if (obj is not Item item)
        {
            return false;
        }

        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Contains(item.Name, query)
               || Contains(item.Barcode, query)
               || Contains(item.Gtin, query);
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

        if (e.Key == Key.Enter && ItemsGrid.SelectedItem is Item)
        {
            e.Handled = true;
            CommitSelection();
        }
    }

    private void CommitSelection()
    {
        if (ItemsGrid.SelectedItem is not Item item)
        {
            return;
        }

        SelectedItem = item;
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
}
