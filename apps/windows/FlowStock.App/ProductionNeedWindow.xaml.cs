using System.Collections.ObjectModel;
using FlowStock.Core.Models;
using System.Windows;
using System.Globalization;

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

    private void LoadRows()
    {
        if (!_services.WpfReadApi.TryGetProductionNeedRows(
                includeZeroNeed: false,
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
                ItemTypeName = string.IsNullOrWhiteSpace(row.ItemTypeName) ? "Без типа" : row.ItemTypeName,
                FreeStockQty = row.FreeStockQty,
                MinStockQty = row.MinStockQty,
                ToCloseOrdersQty = row.ToCloseOrdersQty,
                ToMinStockQty = row.ToMinStockQty,
                TotalToMakeQty = row.TotalToMakeQty
            });
        }

        SummaryTextBlock.Text = $"Позиций: {_rows.Count}.";
    }

    private sealed record ProductionNeedDisplayRow
    {
        public long ItemId { get; init; }
        public string Gtin { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public double FreeStockQty { get; init; }
        public double MinStockQty { get; init; }
        public double ToCloseOrdersQty { get; init; }
        public double ToMinStockQty { get; init; }
        public double TotalToMakeQty { get; init; }
        public string StockDisplay => $"{FormatQty(FreeStockQty)} / {FormatQty(MinStockQty)}";
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }
}
