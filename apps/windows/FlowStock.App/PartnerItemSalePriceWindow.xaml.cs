using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class PartnerItemSalePriceWindow : Window
{
    private const int PageSize = 100;
    private readonly AppServices _services;
    private readonly long? _initialItemId;
    private readonly ObservableCollection<PartnerItemSalePrice> _prices = new();
    private readonly List<Partner> _partners = new();
    private readonly List<Item> _items = new();
    private PartnerItemSalePrice? _selected;
    private int _offset;
    private int _totalCount;

    public PartnerItemSalePriceWindow(AppServices services, long? itemId = null)
    {
        _services = services;
        _initialItemId = itemId;
        InitializeComponent();
        PricesGrid.ItemsSource = _prices;
        LoadLookups();
        ResetForm();
        Loaded += async (_, _) => await LoadPageAsync().ConfigureAwait(true);
    }

    private void LoadLookups()
    {
        if (_services.WpfReadApi.TryGetPartners(out var partners))
        {
            _partners.AddRange(partners.OrderBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        }
        if (_services.WpfReadApi.TryGetItems(null, out var items))
        {
            _items.AddRange(items.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase));
        }

        PartnerCombo.ItemsSource = _partners;
        ItemCombo.ItemsSource = _items;
        FilterPartnerCombo.ItemsSource = _partners;
        FilterItemCombo.ItemsSource = _items;
        if (_initialItemId.HasValue)
        {
            ItemCombo.SelectedItem = _items.FirstOrDefault(row => row.Id == _initialItemId.Value);
            FilterItemCombo.SelectedItem = _items.FirstOrDefault(row => row.Id == _initialItemId.Value);
        }
    }

    private async Task LoadPageAsync()
    {
        try
        {
            var page = await _services.WpfCommercialCatalogApi.GetPricesAsync(
                partnerId: (FilterPartnerCombo.SelectedItem as Partner)?.Id,
                itemId: (FilterItemCombo.SelectedItem as Item)?.Id,
                limit: PageSize,
                offset: _offset).ConfigureAwait(true);
            _prices.Clear();
            foreach (var price in page.Items)
            {
                _prices.Add(price);
            }
            _totalCount = page.TotalCount;
            PageStatusText.Text = _totalCount == 0
                ? "Нет записей"
                : $"{_offset + 1}–{Math.Min(_offset + PageSize, _totalCount)} из {_totalCount}";
            PreviousButton.IsEnabled = _offset > 0;
            NextButton.IsEnabled = _offset + PageSize < _totalCount;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Цены клиентов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (PartnerCombo.SelectedItem is not Partner partner || ItemCombo.SelectedItem is not Item item)
        {
            MessageBox.Show("Выберите контрагента и товар.", "Цены клиентов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var price)
            || price < 0
            || decimal.Round(price, 4) != price)
        {
            MessageBox.Show(
                "Цена должна быть неотрицательным числом с точностью не более четырёх знаков.",
                "Цены клиентов",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _services.WpfCommercialCatalogApi.SavePriceAsync(
                _selected?.Id,
                partner.Id,
                item.Id,
                price,
                IsActiveCheck.IsChecked == true).ConfigureAwait(true);
            ResetForm();
            await LoadPageAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Цены клиентов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            return;
        }
        if (MessageBox.Show(
                "Удалить выбранную неактивную цену?",
                "Цены клиентов",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _services.WpfCommercialCatalogApi.DeletePriceAsync(_selected.Id).ConfigureAwait(true);
            ResetForm();
            await LoadPageAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Цены клиентов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PricesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = PricesGrid.SelectedItem as PartnerItemSalePrice;
        if (_selected == null)
        {
            return;
        }
        PartnerCombo.SelectedItem = _partners.FirstOrDefault(row => row.Id == _selected.PartnerId);
        ItemCombo.SelectedItem = _items.FirstOrDefault(row => row.Id == _selected.ItemId);
        PriceBox.Text = _selected.UnitPriceGross.ToString("0.####", CultureInfo.CurrentCulture);
        IsActiveCheck.IsChecked = _selected.IsActive;
        SaveButton.Content = "Сохранить";
        DeleteButton.IsEnabled = !_selected.IsActive;
    }

    private void ResetForm()
    {
        _selected = null;
        PricesGrid.SelectedItem = null;
        PartnerCombo.SelectedItem = null;
        ItemCombo.SelectedItem = _initialItemId.HasValue
            ? _items.FirstOrDefault(row => row.Id == _initialItemId.Value)
            : null;
        PriceBox.Text = string.Empty;
        IsActiveCheck.IsChecked = true;
        SaveButton.Content = "Добавить";
        DeleteButton.IsEnabled = false;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
        await Task.CompletedTask;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _offset = 0;
        await LoadPageAsync().ConfigureAwait(true);
    }

    private async void ApplyFilter_Click(object sender, RoutedEventArgs e)
    {
        _offset = 0;
        await LoadPageAsync().ConfigureAwait(true);
    }

    private async void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterPartnerCombo.SelectedItem = null;
        FilterItemCombo.SelectedItem = null;
        _offset = 0;
        await LoadPageAsync().ConfigureAwait(true);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        _offset = Math.Max(0, _offset - PageSize);
        await LoadPageAsync().ConfigureAwait(true);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_offset + PageSize < _totalCount)
        {
            _offset += PageSize;
            await LoadPageAsync().ConfigureAwait(true);
        }
    }
}
