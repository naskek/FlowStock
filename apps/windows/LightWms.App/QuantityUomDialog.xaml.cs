using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class QuantityUomDialog : Window
{
    private const string BaseUomCode = "BASE";
    private readonly string _baseUom;
    private readonly ObservableCollection<UomOption> _options = new();

    public double QtyInput { get; private set; }
    public string UomCode { get; private set; } = BaseUomCode;
    public double QtyBase { get; private set; }

    public QuantityUomDialog(string baseUom, IReadOnlyList<ItemPackaging> packagings, double defaultQty, string? defaultUomCode)
    {
        _baseUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom;
        InitializeComponent();

        UomCombo.ItemsSource = _options;
        _options.Add(new UomOption(BaseUomCode, $"{_baseUom} (база)", 1));
        foreach (var packaging in packagings)
        {
            if (!packaging.IsActive)
            {
                continue;
            }

            _options.Add(new UomOption(packaging.Code, packaging.Name, packaging.FactorToBase));
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

        QtyInput = qty;
        UomCode = option.Code;
        QtyBase = qty * option.FactorToBase;
        DialogResult = true;
        Close();
    }

    private void UpdateTotal()
    {
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

    private sealed record UomOption(string Code, string Name, double FactorToBase);
}
