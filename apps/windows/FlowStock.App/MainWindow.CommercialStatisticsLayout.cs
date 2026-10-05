using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsContentLayoutApplied;
    private static bool _commercialStatisticsNestedGridHandlerRegistered;

    private void ApplyCommercialStatisticsContentLayout()
    {
        if (_commercialStatisticsContentLayoutApplied)
        {
            return;
        }

        _commercialStatisticsContentLayoutApplied = true;

        if (StatisticsGroupsBox.Parent is Grid detailsGrid
            && detailsGrid.ColumnDefinitions.Count >= 2)
        {
            detailsGrid.ColumnDefinitions[0].Width = GridLength.Auto;
            detailsGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        }

        if (StatisticsMonthlyGrid.Parent is FrameworkElement monthlyContainer)
        {
            monthlyContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }

        StatisticsMonthlyGrid.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        ConfigureCommercialStatisticsGridColumns(StatisticsMonthlyGrid);
        ConfigureCommercialStatisticsGridColumns(StatisticsGroupsGrid);

        if (!_commercialStatisticsNestedGridHandlerRegistered)
        {
            EventManager.RegisterClassHandler(
                typeof(CommercialStatisticsDrillDownItemsGrid),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(CommercialStatisticsNestedGrid_Loaded));
            _commercialStatisticsNestedGridHandlerRegistered = true;
        }
    }

    private static void CommercialStatisticsNestedGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CommercialStatisticsDrillDownItemsGrid grid)
        {
            ConfigureCommercialStatisticsGridColumns(grid);
        }
    }

    private static void ConfigureCommercialStatisticsGridColumns(DataGrid grid)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        grid.CanUserResizeColumns = true;

        foreach (var column in grid.Columns)
        {
            column.Width = DataGridLength.Auto;
        }
    }
}
