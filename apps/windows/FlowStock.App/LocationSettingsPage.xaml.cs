using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlowStock.Core.Models;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace FlowStock.App;

public partial class LocationSettingsPage : WpfUserControl
{
    private readonly AppServices? _productionServices;
    public bool IsUiPreview => _productionServices is null;
    private AppServices _services => _productionServices
        ?? throw new InvalidOperationException(UiPreviewContext.OperationUnavailable);
    private readonly SettingsPageLoading _loading = null!;
    private readonly Action? _onChanged;
    private readonly ObservableCollection<Location> _locations = new();

    public LocationSettingsPage(AppServices services, Action? onChanged)
    {
        ArgumentNullException.ThrowIfNull(services);
        _productionServices = services;
        _onChanged = onChanged;
        InitializeComponent();
        _loading = new SettingsPageLoading(this, _services.AppLogger);
        LocationsGrid.ItemsSource = _locations;
        _loading.InitializeOnLoaded(() => LoadLocationsAsync());
    }

    private Task LoadLocationsAsync(long? selectedId = null) => _loading.RunAsync(async () =>
    {
        selectedId ??= (LocationsGrid.SelectedItem as Location)?.Id;
        var locations = await Task.Run(() => _services.WpfReadApi.TryGetLocations(out var apiLocations)
            ? apiLocations
            : Array.Empty<Location>());
        _locations.Clear();
        foreach (var location in locations)
        {
            _locations.Add(location);
        }

        var selected = selectedId.HasValue
            ? _locations.FirstOrDefault(location => location.Id == selectedId.Value)
            : null;
        if (selected is not null)
        {
            LocationsGrid.SelectedItem = selected;
            LocationsGrid.ScrollIntoView(selected);
        }

        UpdateButtons();
    });

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        var window = new LocationEditWindow(_services);
        var owner = Window.GetWindow(this);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        if (window.ShowDialog() != true)
        {
            return;
        }

        await LoadLocationsAsync(window.SavedLocationId);
        _onChanged?.Invoke();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        if (LocationsGrid.SelectedItem is not Location selected)
        {
            MessageBox.Show("Выберите место хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = await Task.Run(() => (_services.WpfReadApi.TryGetLocations(out var apiLocations)
                ? apiLocations
                : Array.Empty<Location>())
            .FirstOrDefault(location => location.Id == selected.Id) ?? selected);
        var window = new LocationEditWindow(_services, current);
        var owner = Window.GetWindow(this);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        if (window.ShowDialog() != true)
        {
            return;
        }

        await LoadLocationsAsync(window.SavedLocationId ?? selected.Id);
        _onChanged?.Invoke();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        var selected = LocationsGrid.SelectedItems.Cast<Location>().ToList();
        if (selected.Count == 0 && LocationsGrid.SelectedItem is Location single)
        {
            selected.Add(single);
        }

        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите место хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var message = selected.Count == 1
            ? "Удалить выбранное место хранения?"
            : $"Удалить выбранные места хранения ({selected.Count})?";
        if (MessageBox.Show(message, "Места хранения", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes)
        {
            return;
        }

        var failed = new List<string>();
        foreach (var location in selected)
        {
            try
            {
                var result = await _services.WpfCatalogApi.TryDeleteLocationAsync(location.Id).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось удалить место хранения через сервер.");
                }
            }
            catch (Exception exception)
            {
                failed.Add($"{location.Code}: {exception.Message}");
            }
        }

        await LoadLocationsAsync();
        _onChanged?.Invoke();
        if (failed.Count > 0)
        {
            MessageBox.Show(
                "Не удалось удалить:\n" + string.Join("\n", failed),
                "Места хранения",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;
        await LoadLocationsAsync();
    }

    private void LocationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiPreview) return;
        UpdateButtons();
    }

    private void LocationsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsUiPreview) return;

        if (LocationsGrid.SelectedItem is Location)
        {
            Edit_Click(sender, new RoutedEventArgs());
        }
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsUiPreview) return;

        if (!DeleteKeyGesture.IsDeleteGesture(e) || !LocationsGrid.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        Delete_Click(LocationsGrid, new RoutedEventArgs());
    }

    private void UpdateButtons()
    {
        var hasSelection = LocationsGrid.SelectedItem is Location;
        EditButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }
}
