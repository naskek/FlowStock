using System.Globalization;
using System.Windows;
using FlowStock.App.Services;

namespace FlowStock.App;

public partial class ItemPriceEditWindow : Window
{
    private readonly AppServices _services;
    private readonly CommercialItemPriceRow _item;
    private readonly CommercialPriceGroupRow _priceGroup;

    public bool Saved { get; private set; }

    public ItemPriceEditWindow(AppServices services, CommercialItemPriceRow item, CommercialPriceGroupRow priceGroup)
    {
        _services = services;
        _item = item;
        _priceGroup = priceGroup;
        InitializeComponent();

        ItemNameText.Text = $"{item.ItemName} (ID {item.ItemId})";
        CurrencyBox.Text = string.IsNullOrWhiteSpace(item.Currency) ? priceGroup.Currency : item.Currency;
        if (item.Price.HasValue)
        {
            PriceBox.Text = item.Price.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        ValidFromPicker.SelectedDate = ParseDate(item.ValidFrom) ?? DateTime.Today;
        ValidToPicker.SelectedDate = ParseDate(item.ValidTo);
        CommentBox.Text = item.Comment ?? string.Empty;
        Title = item.HasPrice ? "Изменение цены товара" : "Назначение цены товару";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(PriceBox.Text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || price < 0m)
        {
            MessageBox.Show("Введите корректную цену (не меньше 0).", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currency = CurrencyBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(currency))
        {
            MessageBox.Show("Укажите валюту.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ValidFromPicker.SelectedDate is not DateTime validFromDate)
        {
            MessageBox.Show("Укажите дату начала действия цены.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var validFrom = validFromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? validTo = null;
        if (ValidToPicker.SelectedDate is DateTime validToDate)
        {
            if (validToDate.Date < validFromDate.Date)
            {
                MessageBox.Show("Дата окончания не может быть раньше даты начала.", "Цены", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            validTo = validToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var result = await _services.WpfCommercialApi.TryUpsertItemPriceAsync(
            _item.ItemId,
            _priceGroup.Id,
            price,
            currency,
            validFrom,
            validTo,
            string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim()).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось сохранить цену.", "Цены", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Saved = true;
        DialogResult = true;
        Close();
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
}
