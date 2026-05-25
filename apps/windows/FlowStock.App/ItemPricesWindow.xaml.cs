using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlowStock.App.Services;
using FlowStock.Core.Commercial;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class ItemPricesWindow : Window
{
    private readonly AppServices _services;
    private readonly Item _item;
    private readonly List<ItemPricingOverviewUiRow> _rows = new();
    private CommercialPriceGroupRow? _baseGroup;

    public bool Saved { get; private set; }

    public ItemPricesWindow(AppServices services, Item item)
    {
        _services = services;
        _item = item;
        InitializeComponent();
        LoadData();
    }

    private void LoadData()
    {
        var sku = !string.IsNullOrWhiteSpace(_item.Barcode)
            ? _item.Barcode
            : !string.IsNullOrWhiteSpace(_item.Gtin)
                ? _item.Gtin
                : "—";
        ItemHeaderText.Text = $"{_item.Name} (ID {_item.Id}) · {sku}";

        if (!_services.WpfCommercialApi.TryGetPriceGroups(out var groups) || groups.Count == 0)
        {
            MessageBox.Show("Не удалось загрузить группы цен.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _baseGroup = groups.FirstOrDefault(g => g.IsSystem) ?? groups.FirstOrDefault(g => g.IsDefault);
        if (_baseGroup == null)
        {
            MessageBox.Show("Системная группа «Базовая цена» не найдена.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_services.WpfCommercialApi.TryGetItemPricingOverview(_item.Id, out var overview))
        {
            MessageBox.Show("Не удалось загрузить цены товара.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rows.Clear();
        _rows.AddRange(overview.Select(ItemPricingOverviewUiRow.FromApi));
        GroupsGrid.ItemsSource = _rows;

        var baseRow = _rows.FirstOrDefault(r => r.IsSystem);
        if (baseRow != null)
        {
            BasePriceBox.Text = baseRow.OverridePrice?.ToString("0.####", CultureInfo.InvariantCulture)
                                ?? baseRow.CalculatedPrice?.ToString("0.####", CultureInfo.InvariantCulture)
                                ?? string.Empty;
            BaseCurrencyBox.Text = baseRow.Currency ?? _baseGroup.Currency;
            BaseValidFromPicker.SelectedDate = ParseDate(baseRow.ValidFrom) ?? DateTime.Today;
            BaseValidToPicker.SelectedDate = ParseDate(baseRow.ValidTo);
            BaseCommentBox.Text = baseRow.Comment ?? string.Empty;
            GroupsGrid.SelectedItem = baseRow;
        }
        else
        {
            BaseCurrencyBox.Text = _baseGroup.Currency;
            BaseValidFromPicker.SelectedDate = DateTime.Today;
            GroupsGrid.SelectedIndex = 0;
        }

        UpdateActionButtons();
    }

    private void GroupsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();

    private void UpdateActionButtons()
    {
        if (GroupsGrid.SelectedItem is not ItemPricingOverviewUiRow row)
        {
            SetOverrideButton.IsEnabled = false;
            RemoveOverrideButton.IsEnabled = false;
            return;
        }

        SetOverrideButton.IsEnabled = !row.IsSystem;
        RemoveOverrideButton.IsEnabled = !row.IsSystem && row.HasOverride;
    }

    private async void SaveBasePrice_Click(object sender, RoutedEventArgs e)
    {
        if (_baseGroup == null)
        {
            return;
        }

        if (!decimal.TryParse(BasePriceBox.Text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || price <= 0m)
        {
            MessageBox.Show("Введите корректную базовую цену больше 0.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currency = BaseCurrencyBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(currency))
        {
            MessageBox.Show("Укажите валюту.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (BaseValidFromPicker.SelectedDate is not DateTime validFromDate)
        {
            MessageBox.Show("Укажите дату начала действия цены.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? validTo = null;
        if (BaseValidToPicker.SelectedDate is DateTime validToDate)
        {
            if (validToDate.Date < validFromDate.Date)
            {
                MessageBox.Show("Дата окончания не может быть раньше даты начала.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            validTo = validToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var result = await _services.WpfCommercialApi.TryUpsertItemPriceAsync(
            _item.Id,
            _baseGroup.Id,
            price,
            currency,
            validFromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            validTo,
            string.IsNullOrWhiteSpace(BaseCommentBox.Text) ? null : BaseCommentBox.Text.Trim()).ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            MessageBox.Show(MapError(result.Error), "Цены", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Saved = true;
        LoadData();
        MessageBox.Show("Базовая цена сохранена.", "Цены", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetOverride_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsGrid.SelectedItem is not ItemPricingOverviewUiRow row || row.IsSystem || _baseGroup == null)
        {
            return;
        }

        var catalogRow = new CommercialItemPriceRow
        {
            ItemId = _item.Id,
            ItemName = _item.Name,
            Barcode = _item.Barcode,
            Gtin = _item.Gtin,
            PriceGroupId = row.PriceGroupId,
            Price = row.OverridePrice ?? row.CalculatedPrice,
            Currency = row.Currency ?? _baseGroup.Currency,
            ValidFrom = row.ValidFrom,
            ValidTo = row.ValidTo,
            Comment = row.Comment,
            HasPrice = row.HasOverride
        };

        var group = new CommercialPriceGroupRow
        {
            Id = row.PriceGroupId,
            Name = row.PriceGroupName,
            Currency = row.Currency ?? _baseGroup.Currency,
            IsSystem = row.IsSystem
        };

        var window = new ItemPriceEditWindow(_services, catalogRow, group) { Owner = this };
        if (window.ShowDialog() == true)
        {
            Saved = true;
            LoadData();
        }
    }

    private async void RemoveOverride_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsGrid.SelectedItem is not ItemPricingOverviewUiRow row || row.ItemPriceId is not > 0)
        {
            return;
        }

        if (MessageBox.Show(
                $"Убрать индивидуальную цену для группы «{row.PriceGroupName}»?",
                "Цены",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _services.WpfCommercialApi.TryDeactivateItemPriceAsync(row.ItemPriceId.Value).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось убрать индивидуальную цену.", "Цены", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Saved = true;
        LoadData();
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToDateTime(TimeOnly.MinValue)
            : null;
    }

    private static string MapError(string? error) => error switch
    {
        "PRICE_NOT_FOUND" => "Сначала задайте базовую цену товара.",
        "PRICE_IS_ZERO" => "Цена не может быть нулевой.",
        _ => error ?? "Не удалось сохранить цену."
    };

    private sealed class ItemPricingOverviewUiRow
    {
        public long PriceGroupId { get; init; }
        public string PriceGroupName { get; init; } = string.Empty;
        public bool IsSystem { get; init; }
        public decimal DefaultDiscountPercent { get; init; }
        public decimal DefaultMarkupPercent { get; init; }
        public decimal? OverridePrice { get; init; }
        public decimal? CalculatedPrice { get; init; }
        public string? Currency { get; init; }
        public long? ItemPriceId { get; init; }
        public string? ValidFrom { get; init; }
        public string? ValidTo { get; init; }
        public string? Comment { get; init; }
        public string PriceSource { get; init; } = string.Empty;

        public bool HasOverride => OverridePrice is > 0 && ItemPriceId is > 0 && !IsSystem;

        public string PriceSourceDisplay =>
            PriceSourceKindMapper.FromCode(PriceSource) is { } kind
                ? PriceSourceKindMapper.ToDisplayName(kind)
                : PriceSource;

        public string OverridePriceDisplay => OverridePrice.HasValue
            ? OverridePrice.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "—";

        public string CalculatedPriceDisplay => CalculatedPrice is > 0m
            ? CalculatedPrice.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "не задана";

        public static ItemPricingOverviewUiRow FromApi(CommercialItemPricingOverviewRow row) => new()
        {
            PriceGroupId = row.PriceGroupId,
            PriceGroupName = row.PriceGroupName,
            IsSystem = row.IsSystem,
            DefaultDiscountPercent = row.DefaultDiscountPercent,
            DefaultMarkupPercent = row.DefaultMarkupPercent,
            OverridePrice = row.OverridePrice,
            CalculatedPrice = row.CalculatedPrice,
            Currency = row.Currency,
            ItemPriceId = row.ItemPriceId,
            ValidFrom = row.ValidFrom,
            ValidTo = row.ValidTo,
            Comment = row.Comment,
            PriceSource = row.PriceSource
        };
    }
}
