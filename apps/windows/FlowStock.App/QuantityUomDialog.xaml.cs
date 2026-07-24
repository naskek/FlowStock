using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class QuantityUomDialog : Window
{
    private const string BaseUomCode = "BASE";
    private readonly string _baseUom;
    private readonly double? _availableQty;
    private readonly bool _showAvailableLabel;
    private readonly bool _showCommercialTerms;
    private readonly bool _requirePriceForSave;
    private readonly decimal? _automaticUnitPriceGross;
    private readonly ObservableCollection<UomOption> _options = new();

    public double QtyInput { get; private set; }
    public string UomCode { get; private set; } = BaseUomCode;
    public double QtyBase { get; private set; }
    public bool ChangeUnitPriceGross { get; private set; }
    public decimal? UnitPriceGross { get; private set; }

    public QuantityUomDialog(
        string baseUom,
        IReadOnlyList<ItemPackaging> packagings,
        double defaultQty,
        string? defaultUomCode,
        double? availableQty = null,
        bool showAvailableLabel = false,
        bool showCommercialTerms = false,
        decimal? automaticUnitPriceGross = null,
        string? priceSourceDisplay = null,
        decimal? vatRate = null,
        string? commercialIssue = null,
        decimal? currentUnitPriceGross = null,
        bool commercialTermsLocked = false,
        bool requirePriceForSave = false)
    {
        _baseUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom;
        _availableQty = availableQty;
        _showAvailableLabel = showAvailableLabel;
        _showCommercialTerms = showCommercialTerms;
        _requirePriceForSave = requirePriceForSave;
        _automaticUnitPriceGross = automaticUnitPriceGross;
        InitializeComponent();

        CommercialTermsPanel.Visibility = showCommercialTerms ? Visibility.Visible : Visibility.Collapsed;
        if (showCommercialTerms)
        {
            var shownPrice = currentUnitPriceGross ?? automaticUnitPriceGross;
            PricePreviewText.Text = shownPrice.HasValue
                ? $"Цена с НДС: {shownPrice.Value.ToString("0.####", CultureInfo.CurrentCulture)} руб.{FormatPriceSource(priceSourceDisplay)}"
                : "Автоматическая цена не задана.";
            VatPreviewText.Text = vatRate.HasValue
                ? $"Ставка НДС: {vatRate.Value.ToString("0.####", CultureInfo.CurrentCulture)}%"
                : "Ставка НДС не определена.";
            UnitPriceGrossBox.Text = shownPrice?.ToString("0.####", CultureInfo.CurrentCulture) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(commercialIssue))
            {
                CommercialIssueText.Text = commercialIssue;
                CommercialIssueText.Visibility = Visibility.Visible;
            }
            if (commercialTermsLocked)
            {
                ManualPriceOverrideCheck.IsEnabled = false;
                CommercialLockText.Text = "Цена заблокирована после проведённой отгрузки.";
                CommercialLockText.Visibility = Visibility.Visible;
            }
        }

        UomCombo.ItemsSource = _options;
        _options.Add(new UomOption(BaseUomCode, $"BASE — {_baseUom} (×1)", 1));
        foreach (var packaging in packagings)
        {
            if (!packaging.IsActive)
            {
                continue;
            }

            var factor = packaging.FactorToBase.ToString("0.###", CultureInfo.CurrentCulture);
            var label = $"{packaging.Code} — {packaging.Name} (×{factor})";
            _options.Add(new UomOption(packaging.Code, label, packaging.FactorToBase));
        }

        QtyInput = defaultQty > 0 ? defaultQty : 1;
        QtyBox.Text = QtyInput.ToString(CultureInfo.CurrentCulture);

        var selectedCode = string.IsNullOrWhiteSpace(defaultUomCode) ? BaseUomCode : defaultUomCode.Trim();
        UomCombo.SelectedItem = _options.FirstOrDefault(option => string.Equals(option.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
                                ?? _options.FirstOrDefault();

        QtyBox.TextChanged += (_, _) => UpdateTotal();
        UomCombo.SelectionChanged += (_, _) => UpdateTotal();

        Loaded += (_, _) =>
        {
            QtyBox.Focus();
            QtyBox.SelectAll();
            UpdateTotal();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetQty(out var qty))
        {
            MessageBox.Show("Количество должно быть больше 0.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (UomCombo.SelectedItem is not UomOption option)
        {
            MessageBox.Show("Выберите единицу.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var qtyBase = qty * option.FactorToBase;
        if (_availableQty.HasValue && qtyBase > _availableQty.Value + 0.000001)
        {
            MessageBox.Show(
                $"Количество превышает доступный остаток: доступно {_availableQty.Value.ToString("0.###", CultureInfo.CurrentCulture)} {_baseUom}.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        QtyInput = qty;
        UomCode = option.Code;
        QtyBase = qtyBase;
        if (_showCommercialTerms)
        {
            ChangeUnitPriceGross = ManualPriceOverrideCheck.IsChecked == true;
            if (ChangeUnitPriceGross)
            {
                if (!decimal.TryParse(
                        UnitPriceGrossBox.Text,
                        NumberStyles.Number,
                        CultureInfo.CurrentCulture,
                        out var price)
                    || price < 0
                    || decimal.Round(price, 4) != price)
                {
                    MessageBox.Show(
                        "Цена с НДС должна быть неотрицательным числом с точностью не более четырёх знаков.",
                        "Коммерческие условия",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                UnitPriceGross = price;
            }
            else if (!CommercialLineEditPolicy.CanSaveWithoutManualPrice(
                         _requirePriceForSave,
                         _automaticUnitPriceGross))
            {
                MessageBox.Show(
                    "Автоматическая цена не задана. Укажите цену вручную.",
                    "Коммерческие условия",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private void ManualPriceOverrideCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UnitPriceGrossBox.IsEnabled = ManualPriceOverrideCheck.IsChecked == true
                                      && ManualPriceOverrideCheck.IsEnabled;
        if (UnitPriceGrossBox.IsEnabled)
        {
            UnitPriceGrossBox.Focus();
            UnitPriceGrossBox.SelectAll();
        }
    }

    private void UpdateTotal()
    {
        if (_availableQty.HasValue || _showAvailableLabel)
        {
            if (_availableQty.HasValue)
            {
                TotalText.Text = $"Доступно: {_availableQty.Value.ToString("0.###", CultureInfo.CurrentCulture)} {_baseUom}";
            }
            else
            {
                TotalText.Text = "Доступно: -";
            }
            return;
        }

        if (!TryGetQty(out var qty))
        {
            TotalText.Text = "Итого: -";
            return;
        }

        if (UomCombo.SelectedItem is not UomOption option)
        {
            TotalText.Text = "Итого: -";
            return;
        }

        var total = qty * option.FactorToBase;
        TotalText.Text = $"Итого: {total.ToString("0.###", CultureInfo.CurrentCulture)} {_baseUom}";
    }

    private bool TryGetQty(out double qty)
    {
        return double.TryParse(QtyBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out qty) && qty > 0;
    }

    private static string FormatPriceSource(string? source) =>
        string.IsNullOrWhiteSpace(source) ? string.Empty : $" ({source.Trim()})";

    private sealed record UomOption(string Code, string Name, double FactorToBase);
}
