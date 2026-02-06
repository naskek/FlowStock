using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class ItemRequestsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ItemRequest> _requests = new();
    private readonly Action? _onChanged;

    public ItemRequestsWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        RequestsGrid.ItemsSource = _requests;
        RequestsGrid.SelectionChanged += RequestsGrid_SelectionChanged;
        LoadRequests();
    }

    private void LoadRequests()
    {
        _requests.Clear();
        foreach (var request in _services.DataStore.GetItemRequests(true))
        {
            _requests.Add(request);
        }

        ResolveButton.IsEnabled = RequestsGrid.SelectedItems.Count > 0;
    }

    private void RequestsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ResolveButton.IsEnabled = RequestsGrid.SelectedItems.Count > 0;
    }

    private void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequestsGrid.SelectedItems.Cast<ItemRequest>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите запрос.", "Запросы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var request in selected)
        {
            _services.DataStore.MarkItemRequestResolved(request.Id);
        }

        LoadRequests();
        _onChanged?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
