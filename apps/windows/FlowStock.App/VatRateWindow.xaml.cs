using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class VatRateWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<VatRate> _vatRates = new();
    private VatRate? _selected;

    public VatRateWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        VatRatesGrid.ItemsSource = _vatRates;
        LoadVatRates();
        ResetForm();
    }

    private void LoadVatRates()
    {
        _vatRates.Clear();
        var rates = _services.WpfCatalogApi.TryGetVatRates(includeInactive: true, out var apiRates)
            ? apiRates
            : Array.Empty<VatRate>();
        foreach (var rate in rates)
        {
            _vatRates.Add(rate);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите название ставки НДС.", "Ставки НДС", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseRate(RateBox.Text, out var rate))
        {
            return;
        }

        if (!int.TryParse(SortOrderBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortOrder))
        {
            MessageBox.Show("Сортировка должна быть целым числом.", "Ставки НДС", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var model = new VatRate
        {
            Id = _selected?.Id ?? 0,
            Name = name,
            Rate = rate,
            IsActive = IsActiveCheck.IsChecked == true,
            SortOrder = sortOrder
        };

        var result = _selected == null
            ? await CreateAsync(model).ConfigureAwait(true)
            : await UpdateAsync(model).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось сохранить ставку НДС.", "Ставки НДС", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadVatRates();
        ResetForm();
    }

    private async Task<(bool IsSuccess, string? Error)> CreateAsync(VatRate model)
    {
        var result = await _services.WpfCatalogApi.TryCreateVatRateAsync(model).ConfigureAwait(true);
        return (result.IsSuccess, result.Error);
    }

    private Task<(bool IsSuccess, string? Error)> UpdateAsync(VatRate model)
    {
        return _services.WpfCatalogApi.TryUpdateVatRateAsync(model);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            return;
        }

        if (MessageBox.Show(
                $"Удалить ставку НДС «{_selected.Name}»?",
                "Ставки НДС",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _services.WpfCatalogApi.TryDeleteVatRateAsync(_selected.Id).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось удалить ставку НДС.", "Ставки НДС", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadVatRates();
        ResetForm();
    }

    private void VatRatesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = VatRatesGrid.SelectedItem as VatRate;
        if (_selected == null)
        {
            return;
        }

        NameBox.Text = _selected.Name;
        RateBox.Text = _selected.Rate.ToString("0.####", CultureInfo.CurrentCulture);
        SortOrderBox.Text = _selected.SortOrder.ToString(CultureInfo.InvariantCulture);
        IsActiveCheck.IsChecked = _selected.IsActive;
        SaveButton.Content = "Сохранить";
        DeleteButton.IsEnabled = true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
    }

    private void ResetForm()
    {
        _selected = null;
        VatRatesGrid.SelectedItem = null;
        NameBox.Text = string.Empty;
        RateBox.Text = string.Empty;
        SortOrderBox.Text = "0";
        IsActiveCheck.IsChecked = true;
        SaveButton.Content = "Добавить";
        DeleteButton.IsEnabled = false;
    }

    private static bool TryParseRate(string? text, out decimal rate)
    {
        var raw = text?.Trim() ?? string.Empty;
        if ((!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out rate)
             && !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out rate))
            || rate < 0
            || decimal.Round(rate, 4) != rate)
        {
            MessageBox.Show(
                "Ставка должна быть неотрицательным числом с точностью не более четырёх знаков.",
                "Ставки НДС",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e) || !VatRatesGrid.IsKeyboardFocusWithin || _selected == null)
        {
            return;
        }

        e.Handled = true;
        Delete_Click(VatRatesGrid, new RoutedEventArgs());
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
