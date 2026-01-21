using System.Globalization;
using System.Windows;

namespace LightWms.App;

public partial class QuantityDialog : Window
{
    public double Qty { get; private set; }

    public QuantityDialog(double defaultQty)
    {
        InitializeComponent();
        Qty = defaultQty > 0 ? defaultQty : 1;
        QtyBox.Text = Qty.ToString(CultureInfo.CurrentCulture);

        Loaded += (_, _) =>
        {
            QtyBox.Focus();
            QtyBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(QtyBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var qty) || qty <= 0)
        {
            MessageBox.Show("Количество должно быть больше 0.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Qty = qty;
        DialogResult = true;
        Close();
    }
}
