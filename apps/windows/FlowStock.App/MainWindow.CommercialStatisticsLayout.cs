using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class MainWindow
{
    private const double CommercialStatisticsMonthlyPaneMinWidth = 500;
    private const double CommercialStatisticsMonthlyPaneInitialWidth = 540;

    private bool _commercialStatisticsContentLayoutApplied;
    private static bool _commercialStatisticsNestedGridHandlerRegistered;
    private GridSplitter? _commercialStatisticsDetailsSplitter;

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
            ConfigureCommercialStatisticsDetailsGrid(detailsGrid);
        }

        if (StatisticsMonthlyGrid.Parent is FrameworkElement monthlyContainer)
        {
            monthlyContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            monthlyContainer.MinWidth = CommercialStatisticsMonthlyPaneMinWidth;
        }

        StatisticsMonthlyGrid.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        StatisticsMonthlyGrid.MinWidth = CommercialStatisticsMonthlyPaneMinWidth;
        ConfigureCommercialStatisticsMonthlyColumns();
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

    private void ConfigureCommercialStatisticsDetailsGrid(Grid detailsGrid)
    {
        var monthlyColumn = detailsGrid.ColumnDefinitions[0];
        monthlyColumn.MinWidth = CommercialStatisticsMonthlyPaneMinWidth;
        monthlyColumn.Width = new GridLength(CommercialStatisticsMonthlyPaneInitialWidth);

        if (detailsGrid.ColumnDefinitions.Count == 2)
        {
            var detailsColumn = detailsGrid.ColumnDefinitions[1];
            detailsColumn.Width = new GridLength(1, GridUnitType.Star);

            detailsGrid.ColumnDefinitions.Insert(
                1,
                new ColumnDefinition
                {
                    Width = new GridLength(6)
                });

            Grid.SetColumn(StatisticsGroupsBox, 2);

            _commercialStatisticsDetailsSplitter = new GridSplitter
            {
                Width = 6,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = true
            };
            Grid.SetColumn(_commercialStatisticsDetailsSplitter, 1);
            detailsGrid.Children.Add(_commercialStatisticsDetailsSplitter);
        }
        else
        {
            detailsGrid.ColumnDefinitions[^1].Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void ConfigureCommercialStatisticsMonthlyColumns()
    {
        ConfigureCommercialStatisticsGridColumns(StatisticsMonthlyGrid);
        if (StatisticsMonthlyGrid.Columns.Count < 5)
        {
            return;
        }

        StatisticsMonthlyGrid.Columns[0].MinWidth = 72;
        StatisticsMonthlyGrid.Columns[1].MinWidth = 92;
        StatisticsMonthlyGrid.Columns[2].MinWidth = 118;
        StatisticsMonthlyGrid.Columns[3].MinWidth = 118;
        StatisticsMonthlyGrid.Columns[4].MinWidth = 108;
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
