using System.Globalization;
using System.Windows;

namespace FlowStock.App;

public partial class OrderMarkingExportPreviewWindow : Window
{
    public OrderMarkingExportPreviewWindow(OrderMarkingExportPreviewApiResult preview)
    {
        InitializeComponent();
        HeaderText.Text = $"Заказ {preview.OrderRef} (id {preview.OrderId})";
        LinesGrid.ItemsSource = preview.Lines
            .Select(line => new OrderMarkingExportPreviewRow(line))
            .ToList();
        SummaryText.Text =
            $"Строк: {preview.LineCount} | всего к маркировке: {FormatQty(preview.TotalQty)}";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class OrderMarkingExportPreviewRow
{
    public OrderMarkingExportPreviewRow(OrderMarkingExportPreviewLineApiResult line)
    {
        ItemName = line.ItemName;
        Gtin = line.Gtin;
        QtyDisplay = FormatQty(line.Qty);
        HuDisplay = line.HuCount.ToString(CultureInfo.InvariantCulture);
        HuCodesDisplay = line.HuCodes.Count == 0
            ? "—"
            : string.Join(", ", line.HuCodes);
    }

    public string ItemName { get; }

    public string Gtin { get; }

    public string QtyDisplay { get; }

    public string HuDisplay { get; }

    public string HuCodesDisplay { get; }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);
}
