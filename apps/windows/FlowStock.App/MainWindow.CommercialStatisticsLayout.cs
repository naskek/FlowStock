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
    private DockPanel? _commercialStatisticsPagerPanel;

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

        ConfigureCommercialStatisticsNavigationLayout();

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

    private void ConfigureCommercialStatisticsNavigationLayout()
    {
        StatisticsAllPeriodButton.Content = "Показать весь период";
        StatisticsAllPeriodButton.ToolTip =
            "Снять выбор месяца и показать статистику за весь выбранный период";
        StatisticsAllPeriodButton.Margin = new Thickness(0, 8, 0, 0);
        StatisticsAllPeriodButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;

        if (StatisticsPreviousPageButton.Parent is System.Windows.Controls.Panel pagerButtons
            && pagerButtons.Parent is DockPanel pagerPanel)
        {
            _commercialStatisticsPagerPanel = pagerPanel;
        }

        StatisticsPreviousPageButton.IsEnabledChanged += StatisticsPaginationButton_IsEnabledChanged;
        StatisticsNextPageButton.IsEnabledChanged += StatisticsPaginationButton_IsEnabledChanged;
        StatisticsAllPeriodButton.IsEnabledChanged += StatisticsAllPeriodButton_IsEnabledChanged;

        if (StatisticsAllPeriodButton.Parent is System.Windows.Controls.Panel oldParent)
        {
            oldParent.Children.Remove(StatisticsAllPeriodButton);
        }

        var monthlyBox = FindVisualAncestor<System.Windows.Controls.GroupBox>(StatisticsMonthlyGrid);
        if (monthlyBox is not null && ReferenceEquals(monthlyBox.Content, StatisticsMonthlyGrid))
        {
            monthlyBox.Content = null;

            var monthlyLayout = new Grid();
            monthlyLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            monthlyLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(StatisticsMonthlyGrid, 0);
            monthlyLayout.Children.Add(StatisticsMonthlyGrid);

            Grid.SetRow(StatisticsAllPeriodButton, 1);
            monthlyLayout.Children.Add(StatisticsAllPeriodButton);

            monthlyBox.Content = monthlyLayout;
        }

        UpdateCommercialStatisticsPaginationVisibility();
        UpdateCommercialStatisticsWholePeriodButtonVisibility();
    }

    private void StatisticsPaginationButton_IsEnabledChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        UpdateCommercialStatisticsPaginationVisibility();
    }

    private void StatisticsAllPeriodButton_IsEnabledChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        UpdateCommercialStatisticsWholePeriodButtonVisibility();
    }

    private void UpdateCommercialStatisticsPaginationVisibility()
    {
        var visibility = StatisticsPreviousPageButton.IsEnabled || StatisticsNextPageButton.IsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_commercialStatisticsPagerPanel is not null)
        {
            _commercialStatisticsPagerPanel.Visibility = visibility;
            return;
        }

        StatisticsPreviousPageButton.Visibility = visibility;
        StatisticsNextPageButton.Visibility = visibility;
        StatisticsPageText.Visibility = visibility;
    }

    private void UpdateCommercialStatisticsWholePeriodButtonVisibility()
    {
        StatisticsAllPeriodButton.Visibility = StatisticsAllPeriodButton.IsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
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
