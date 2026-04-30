using System.Collections.ObjectModel;
using FlowStock.Core.Models;
using System.Windows;

namespace FlowStock.App;

public partial class ProductionNeedWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ProductionNeedDisplayRow> _rows = new();

    public ProductionNeedWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        RowsGrid.ItemsSource = _rows;
        Loaded += ProductionNeedWindow_Loaded;
    }

    private void ProductionNeedWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadRows();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadRows();
    }

    private void ShowAllRowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        LoadRows();
    }

    private void LoadRows()
    {
        if (!_services.WpfReadApi.TryGetProductionNeedRows(
                includeZeroNeed: ShowAllRowsCheckBox.IsChecked == true,
                out var rows))
        {
            MessageBox.Show(
                "Не удалось загрузить отчет потребности производства. Проверьте доступность FlowStock Server API.",
                "Потребность производства",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _rows.Clear();
        foreach (var row in rows)
        {
            _rows.Add(new ProductionNeedDisplayRow
            {
                ItemId = row.ItemId,
                Gtin = string.IsNullOrWhiteSpace(row.Gtin) ? "-" : row.Gtin,
                ItemName = row.ItemName,
                ItemTypeName = string.IsNullOrWhiteSpace(row.ItemTypeName) ? "—" : row.ItemTypeName,
                PhysicalStockQty = row.PhysicalStockQty,
                ActiveCustomerOrderOpenQty = row.ActiveCustomerOrderOpenQty,
                ReservedCustomerOrderQty = row.ReservedCustomerOrderQty,
                FreeStockQty = row.FreeStockQty,
                MinStockQty = row.MinStockQty,
                ProductionNeedQty = row.ProductionNeedQty
            });
        }

        var modeText = ShowAllRowsCheckBox.IsChecked == true
            ? "Все строки"
            : "Только строки с потребностью";
        SummaryTextBlock.Text = $"Позиций: {_rows.Count}. {modeText}.";
    }

    private sealed record ProductionNeedDisplayRow
    {
        public long ItemId { get; init; }
        public string Gtin { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public double PhysicalStockQty { get; init; }
        public double ActiveCustomerOrderOpenQty { get; init; }
        public double ReservedCustomerOrderQty { get; init; }
        public double FreeStockQty { get; init; }
        public double MinStockQty { get; init; }
        public double ProductionNeedQty { get; init; }
    }
}
