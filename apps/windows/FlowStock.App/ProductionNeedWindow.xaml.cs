using System.Collections.ObjectModel;
using FlowStock.Core.Models;
using System.Windows;
using System.Windows.Data;
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
        ConfigureGrouping();
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
        var showAllRows = ShowAllRowsCheckBox.IsChecked == true;
        var today = DateTime.Today;
        foreach (var row in rows)
        {
            if (!showAllRows
                && (row.NeedDate.Date != today || row.TotalToMakeQty <= 0))
            {
                continue;
            }

            _rows.Add(new ProductionNeedDisplayRow
            {
                NeedDate = row.NeedDate.Date,
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

        var modeText = showAllRows
            ? "Все даты и строки"
            : "Сегодня, только строки с потребностью";
        SummaryTextBlock.Text = $"Позиций: {_rows.Count}. {modeText}.";
    }

    private void ConfigureGrouping()
    {
        if (CollectionViewSource.GetDefaultView(_rows) is not ListCollectionView view)
        {
            return;
        }

        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProductionNeedDisplayRow.NeedDateGroupLabel)));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProductionNeedDisplayRow.ItemTypeGroupName)));
    }

    private sealed record ProductionNeedDisplayRow
    {
        public DateTime NeedDate { get; init; }
        public long ItemId { get; init; }
        public string Gtin { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string ItemTypeName { get; init; } = string.Empty;
        public double FreeStockQty { get; init; }
        public double MinStockQty { get; init; }
        public double ToCloseOrdersQty { get; init; }
        public double ToMinStockQty { get; init; }
        public double TotalToMakeQty { get; init; }
        public string NeedDateGroupLabel => NeedDate.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        public string ItemTypeGroupName => string.IsNullOrWhiteSpace(ItemTypeName) ? "Без типа" : ItemTypeName;
        public string StockDisplay => $"{FormatQty(FreeStockQty)} / {FormatQty(MinStockQty)}";
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }
}
