using System.Globalization;
using System.Windows;
using FlowStock.App.Services;

namespace FlowStock.App;

public partial class PriceGroupEditWindow : Window
{
    private readonly AppServices _services;
    private readonly CommercialPriceGroupRow? _group;

    public PriceGroupEditWindow(AppServices services, CommercialPriceGroupRow? group = null)
    {
        _services = services;
        _group = group;
        InitializeComponent();

        VatModeCombo.ItemsSource = new[]
        {
            new VatModeOption("INCLUDED", "С НДС"),
            new VatModeOption("EXCLUDED", "Без НДС"),
            new VatModeOption("NO_VAT", "НДС не применяется")
        };
        VatModeCombo.SelectedItem = VatModeCombo.Items.Cast<VatModeOption>().First(o => o.Code == "INCLUDED");

        if (_group == null)
        {
            IdBox.Text = "(новая)";
            DiscountBox.Text = "0";
            MarkupBox.Text = "0";
            return;
        }

        IdBox.Text = _group.Id.ToString(CultureInfo.InvariantCulture);
        NameBox.Text = _group.Name;
        DescriptionBox.Text = _group.Description ?? string.Empty;
        CurrencyBox.Text = _group.Currency;
        VatModeCombo.SelectedItem = VatModeCombo.Items.Cast<VatModeOption>().FirstOrDefault(o => o.Code == _group.VatMode)
            ?? VatModeCombo.SelectedItem;
        DiscountBox.Text = _group.DefaultDiscountPercent.ToString("0.####", CultureInfo.InvariantCulture);
        MarkupBox.Text = _group.DefaultMarkupPercent.ToString("0.####", CultureInfo.InvariantCulture);
        IsActiveCheck.IsChecked = _group.IsActive;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите наименование группы цен.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(DiscountBox.Text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var discount)
            || discount < 0m || discount > 100m)
        {
            MessageBox.Show("Введите корректную скидку от 0 до 100%.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(MarkupBox.Text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var markup)
            || markup < 0m || markup > 100m)
        {
            MessageBox.Show("Введите корректную наценку от 0 до 100%.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var vatMode = (VatModeCombo.SelectedItem as VatModeOption)?.Code ?? "INCLUDED";
        var currency = string.IsNullOrWhiteSpace(CurrencyBox.Text) ? "RUB" : CurrencyBox.Text.Trim();
        var description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        var isActive = IsActiveCheck.IsChecked != false;

        try
        {
            if (_group == null)
            {
                var result = await _services.WpfCommercialApi.TryCreatePriceGroupAsync(
                    name,
                    description,
                    currency,
                    vatMode,
                    isDefault: false,
                    defaultDiscountPercent: discount,
                    defaultMarkupPercent: markup).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось создать группу цен.");
                }
            }
            else
            {
                var result = await _services.WpfCommercialApi.TryUpdatePriceGroupAsync(
                    _group.Id,
                    name,
                    description,
                    currency,
                    vatMode,
                    isDefault: false,
                    isActive,
                    discount,
                    markup).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось обновить группу цен.");
                }
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record VatModeOption(string Code, string Name);
}

