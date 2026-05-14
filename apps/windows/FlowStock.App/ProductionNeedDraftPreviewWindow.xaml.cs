using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace FlowStock.App;

public partial class ProductionNeedDraftPreviewWindow : Window
{
    private readonly ObservableCollection<ProductionNeedDraftLineRow> _rows;

    public ProductionNeedDraftPreviewWindow(IReadOnlyList<ProductionNeedDraftLineRow> rows)
    {
        InitializeComponent();
        _rows = new ObservableCollection<ProductionNeedDraftLineRow>(
            (rows ?? Array.Empty<ProductionNeedDraftLineRow>())
            .Select(row => new ProductionNeedDraftLineRow
            {
                ItemId = row.ItemId,
                Gtin = row.Gtin,
                ItemName = row.ItemName,
                QtyOrdered = row.QtyOrdered
            }));
        RowsGrid.ItemsSource = _rows;
    }

    public IReadOnlyList<ProductionNeedDraftLineRow> GetConfirmedLines()
    {
        return _rows
            .Where(row => row.QtyOrdered > 0.000001d)
            .Select(row => new ProductionNeedDraftLineRow
            {
                ItemId = row.ItemId,
                Gtin = row.Gtin,
                ItemName = row.ItemName,
                QtyOrdered = row.QtyOrdered
            })
            .ToArray();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            if (double.IsNaN(row.QtyOrdered) || double.IsInfinity(row.QtyOrdered) || row.QtyOrdered < 0)
            {
                MessageBox.Show(
                    "Количество должно быть неотрицательным числом.",
                    "Потребность производства",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        if (_rows.All(row => row.QtyOrdered <= 0.000001d))
        {
            MessageBox.Show(
                "Нет строк с количеством больше нуля.",
                "Потребность производства",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}

public sealed class ProductionNeedDraftLineRow
{
    public long ItemId { get; init; }
    public string Gtin { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public double QtyOrdered { get; set; }
    public string QtyDisplay => QtyOrdered.ToString("0.###", CultureInfo.CurrentCulture);
}
