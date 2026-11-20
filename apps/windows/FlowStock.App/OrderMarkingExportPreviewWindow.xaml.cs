using System.Globalization;
using System.Windows;

namespace FlowStock.App;

public partial class OrderMarkingExportPreviewWindow : Window
{
    public OrderMarkingExportPreviewWindow(OrderMarkingExportPreviewApiResult preview)
    {
        InitializeComponent();
        HeaderText.Text = $"Заказ {preview.OrderRef} (id {preview.OrderId})";
        _rows = preview.Lines
            .Select(line => new OrderMarkingExportPreviewRow(line))
            .ToList();
        LinesGrid.ItemsSource = _rows;
        SummaryText.Text =
            $"Строк: {preview.LineCount} | всего: {FormatQty(preview.TotalQty)}";
    }

    private readonly List<OrderMarkingExportPreviewRow> _rows;
    public long[] SelectedLineIds => _rows.Where(row => row.IsSelected).Select(row => row.OrderLineId).ToArray();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLineIds.Length == 0)
        {
            MessageBox.Show(this, "Выберите хотя бы одну строку.", "Excel ЧЗ");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class OrderMarkingExportPreviewRow
{
    public OrderMarkingExportPreviewRow(OrderMarkingExportPreviewLineApiResult line)
    {
        OrderLineId = line.OrderLineId;
        ItemName = line.ItemName;
        Gtin = line.Gtin;
        QtyDisplay = FormatQty(line.Qty);
        HuDisplay = line.HuCount.ToString(CultureInfo.InvariantCulture);
        HuCodesDisplay = line.HuCodes.Count == 0
            ? "—"
            : string.Join(", ", line.HuCodes);
    }

    public long OrderLineId { get; }
    public bool IsSelected { get; set; } = true;
    public string ItemName { get; }

    public string Gtin { get; }

    public string QtyDisplay { get; }

    public string HuDisplay { get; }

    public string HuCodesDisplay { get; }

    private static string FormatQty(double qty) =>
        qty.ToString("0.######", CultureInfo.InvariantCulture);
}
