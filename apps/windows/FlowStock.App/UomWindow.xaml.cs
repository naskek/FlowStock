using System.Collections.ObjectModel;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class UomWindow : Window
{
    private readonly AppServices _services;
    private readonly Action? _onChanged;
    private readonly ObservableCollection<Uom> _uoms = new();
    private Uom? _selectedUom;

    public UomWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        UomsGrid.ItemsSource = _uoms;
        LoadUoms();
        UpdateDeleteButton();
    }

    private void LoadUoms()
    {
        _uoms.Clear();
        var uoms = _services.WpfCatalogApi.TryGetUoms(out var apiUoms)
            ? apiUoms
            : Array.Empty<Uom>();
        foreach (var uom in uoms)
        {
            _uoms.Add(uom);
        }

        UpdateDeleteButton();
    }

    private async void AddUom_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UomNameBox.Text))
        {
            MessageBox.Show("Введите единицу измерения.", "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryCreateUomAsync(UomNameBox.Text.Trim()).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось создать единицу измерения через сервер.");
            }

            UomNameBox.Text = string.Empty;
            LoadUoms();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteUom_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUom == null)
        {
            MessageBox.Show("Выберите единицу измерения.", "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить единицу измерения \"{_selectedUom.Name}\"?",
            "Ед. измерения",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryDeleteUomAsync(_selectedUom.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось удалить единицу измерения через сервер.");
            }

            LoadUoms();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UomsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedUom = UomsGrid.SelectedItem as Uom;
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        if (DeleteUomButton != null)
        {
            DeleteUomButton.IsEnabled = _selectedUom != null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

