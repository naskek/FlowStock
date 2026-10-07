using System.Globalization;
using System.Windows;

namespace FlowStock.App;

public partial class CustomPalletCapacityDialog : Window
{
    public CustomPalletCapacityDialog(string itemName, int index, int total)
    {
        InitializeComponent();
        ItemNameText.Text = string.IsNullOrWhiteSpace(itemName) ? "Товар без названия" : itemName.Trim();
        ProgressText.Text = total > 1 ? $"Позиция {index} из {total}" : "Выбранная позиция";
        Loaded += (_, _) =>
        {
            CapacityTextBox.Focus();
            CapacityTextBox.SelectAll();
        };
    }

    public double MaxQtyPerHu { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var raw = (CapacityTextBox.Text ?? string.Empty).Trim();
        if ((!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
             && !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            || !double.IsFinite(value)
            || value <= 0)
        {
            MessageBox.Show(
                "Введите положительный максимум на палету.",
                "Нестандартная палетизация",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            CapacityTextBox.Focus();
            CapacityTextBox.SelectAll();
            return;
        }

        MaxQtyPerHu = value;
        DialogResult = true;
    }
}
