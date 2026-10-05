using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WpfBinding = System.Windows.Data.Binding;

namespace FlowStock.App;

public partial class MainWindow
{
    private readonly Dictionary<long, CommercialStatisticsPartnerDrillDownState> _commercialStatisticsDrillDownCache = [];
    private DependencyPropertyDescriptor? _commercialStatisticsGroupsItemsSourceDescriptor;
    private bool _commercialStatisticsDrillDownInitialized;
    private DataGridTemplateColumn? _commercialStatisticsGroupDisplayColumn;

    private void ApplyCommercialStatisticsDrillDownEnhancements()
    {
        if (_commercialStatisticsDrillDownInitialized || !_commercialStatisticsAdvancedUiInitialized)
        {
            return;
        }

        _commercialStatisticsDrillDownInitialized = true;
        ConfigureCommercialStatisticsGroupingChoices();
        ConfigureCommercialStatisticsGroupDisplayColumn();
        ConfigureCommercialStatisticsPartnerRowDetails();

        StatisticsGroupCombo.SelectionChanged += StatisticsDrillDownGrouping_SelectionChanged;
        StatisticsGroupsGrid.MouseDoubleClick += StatisticsGroupsGrid_MouseDoubleClick;

        _commercialStatisticsGroupsItemsSourceDescriptor =
            DependencyPropertyDescriptor.FromProperty(
                ItemsControl.ItemsSourceProperty,
                typeof(DataGrid));
        _commercialStatisticsGroupsItemsSourceDescriptor?.AddValueChanged(
            StatisticsGroupsGrid,
            StatisticsDrillDownItemsSource_Changed);

        UpdateCommercialStatisticsGroupPresentation();
    }

    private void ConfigureCommercialStatisticsGroupingChoices()
    {
        ComboBoxItem? gtinItem = null;
        foreach (var item in StatisticsGroupCombo.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString();
            if (string.Equals(tag, "item", StringComparison.OrdinalIgnoreCase))
            {
                item.Content = "Товар / GTIN";
            }
            else if (string.Equals(tag, "gtin", StringComparison.OrdinalIgnoreCase))
            {
                gtinItem = item;
            }
        }

        if (gtinItem is not null)
        {
            var wasSelected = ReferenceEquals(StatisticsGroupCombo.SelectedItem, gtinItem);
            StatisticsGroupCombo.Items.Remove(gtinItem);
            if (wasSelected)
            {
                StatisticsGroupCombo.SelectedIndex = 0;
            }
        }
    }

    private void ConfigureCommercialStatisticsGroupDisplayColumn()
    {
        if (StatisticsGroupsGrid.Columns.Count == 0)
        {
            return;
        }

        var column = new DataGridTemplateColumn
        {
            Header = "Контрагент",
            CellTemplate = BuildCommercialStatisticsGroupCellTemplate(showExpandIndicator: true),
            Width = DataGridLength.Auto,
            MinWidth = 180
        };
        StatisticsGroupsGrid.Columns.RemoveAt(0);
        StatisticsGroupsGrid.Columns.Insert(0, column);
        _commercialStatisticsGroupDisplayColumn = column;
    }

    private DataTemplate BuildCommercialStatisticsGroupCellTemplate(bool showExpandIndicator)
    {
        var converter = new CommercialStatisticsGroupDisplayLabelConverter(this);
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        panelFactory.SetValue(StackPanel.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);

        if (showExpandIndicator)
        {
            panelFactory.SetValue(
                FrameworkElement.ToolTipProperty,
                "Двойной клик — показать или скрыть товары контрагента");

            var arrowFactory = new FrameworkElementFactory(typeof(TextBlock));
            arrowFactory.SetValue(TextBlock.WidthProperty, 18d);
            arrowFactory.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 4, 0));
            arrowFactory.SetValue(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            arrowFactory.SetBinding(
                TextBlock.TextProperty,
                new WpfBinding(nameof(DataGridRow.DetailsVisibility))
                {
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor,
                        typeof(DataGridRow),
                        1),
                    Converter = CommercialStatisticsRowDetailsArrowConverter.Instance
                });
            panelFactory.AppendChild(arrowFactory);
        }

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        textFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        textFactory.SetBinding(
            TextBlock.TextProperty,
            new WpfBinding(".") { Converter = converter });
        textFactory.SetBinding(
            FrameworkElement.ToolTipProperty,
            new WpfBinding(".") { Converter = converter });
        panelFactory.AppendChild(textFactory);

        return new DataTemplate
        {
            VisualTree = panelFactory
        };
    }

    private void ConfigureCommercialStatisticsPartnerRowDetails()
    {
        var viewFactory = new FrameworkElementFactory(typeof(CommercialStatisticsPartnerDrillDownView));
        viewFactory.SetBinding(
            FrameworkElement.DataContextProperty,
            new WpfBinding(nameof(DataGridRow.Tag))
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.FindAncestor,
                    typeof(DataGridRow),
                    1)
            });
        StatisticsGroupsGrid.RowDetailsTemplate = new DataTemplate
        {
            VisualTree = viewFactory
        };
        StatisticsGroupsGrid.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Collapsed;
    }

    private void StatisticsDrillDownGrouping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _commercialStatisticsDrillDownCache.Clear();
        CollapseCommercialStatisticsDrillDownRows();
        UpdateCommercialStatisticsGroupPresentation();
    }

    private void StatisticsDrillDownItemsSource_Changed(object? sender, EventArgs e)
    {
        _commercialStatisticsDrillDownCache.Clear();
        UpdateCommercialStatisticsGroupPresentation();
    }

    private void UpdateCommercialStatisticsGroupPresentation()
    {
        if (_commercialStatisticsGroupDisplayColumn is null)
        {
            return;
        }

        var groupBy = GetCommercialStatisticsGroupingTag();
        _commercialStatisticsGroupDisplayColumn.Header = groupBy switch
        {
            "partner" => "Контрагент",
            "item" => "Товар / GTIN",
            "brand" => "Бренд",
            "volume" => "Фасовка",
            _ => "Группа"
        };
        _commercialStatisticsGroupDisplayColumn.CellTemplate =
            BuildCommercialStatisticsGroupCellTemplate(
                showExpandIndicator: string.Equals(groupBy, "partner", StringComparison.OrdinalIgnoreCase));
    }

    private async void StatisticsGroupsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!string.Equals(GetCommercialStatisticsGroupingTag(), "partner", StringComparison.OrdinalIgnoreCase)
            || e.OriginalSource is not DependencyObject originalSource)
        {
            return;
        }

        var sourceGrid = FindVisualAncestor<DataGrid>(originalSource);
        if (!ReferenceEquals(sourceGrid, StatisticsGroupsGrid))
        {
            return;
        }

        var row = ItemsControl.ContainerFromElement(StatisticsGroupsGrid, originalSource) as DataGridRow;
        if (row?.Item is not WpfCommercialStatisticsGroup group
            || !long.TryParse(group.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partnerId)
            || partnerId <= 0)
        {
            return;
        }

        e.Handled = true;
        if (row.DetailsVisibility == Visibility.Visible)
        {
            row.DetailsVisibility = Visibility.Collapsed;
            return;
        }

        if (!_commercialStatisticsDrillDownCache.TryGetValue(partnerId, out var state))
        {
            state = new CommercialStatisticsPartnerDrillDownState();
            _commercialStatisticsDrillDownCache[partnerId] = state;
        }

        row.Tag = state;
        row.DetailsVisibility = Visibility.Visible;
        if (!state.IsLoaded && !state.IsLoading)
        {
            await LoadCommercialStatisticsPartnerDrillDownAsync(partnerId, group, state).ConfigureAwait(true);
        }
    }

    private async Task LoadCommercialStatisticsPartnerDrillDownAsync(
        long partnerId,
        WpfCommercialStatisticsGroup parent,
        CommercialStatisticsPartnerDrillDownState state)
    {
        var filters = BuildCurrentCommercialStatisticsFilters(out var validationError);
        if (filters is null)
        {
            state.Fail(validationError);
            return;
        }

        state.BeginLoading();
        try
        {
            var groups = new List<WpfCommercialStatisticsGroup>();
            var offset = 0;
            var totalCount = int.MaxValue;
            while (offset < totalCount)
            {
                var request = CommercialStatisticsDrillDown.BuildPartnerItemsRequest(
                    filters,
                    _commercialStatisticsState,
                    partnerId,
                    offset);
                var result = await _services.WpfCommercialStatisticsApi.GetAsync(request).ConfigureAwait(true);
                if (!string.Equals(result.GroupBy, "item", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Сервер вернул неожиданную группировку drill-down.");
                }

                totalCount = Math.Max(0, result.Groups.TotalCount);
                groups.AddRange(result.Groups.Items);
                if (result.Groups.Items.Count == 0)
                {
                    break;
                }
                offset += result.Groups.Items.Count;
            }

            var catalog = _items.ToDictionary(
                item => item.Id,
                item => new CommercialStatisticsDrillDownItemCatalogEntry(
                    item.Id,
                    item.Name,
                    item.Gtin,
                    item.Volume));
            var sections = CommercialStatisticsDrillDown.GroupByVolume(groups, catalog);
            var warning = CommercialStatisticsDrillDown.AmountsMatch(parent.Amounts, groups)
                ? null
                : "Внимание: суммы дочерней детализации не совпали с агрегатом контрагента.";
            state.Complete(sections, warning);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("commercial statistics partner drill-down failed", ex);
            state.Fail("Не удалось загрузить товарный состав: " + ex.Message);
        }
    }

    private void CollapseCommercialStatisticsDrillDownRows()
    {
        foreach (var item in StatisticsGroupsGrid.Items)
        {
            if (StatisticsGroupsGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
            {
                row.DetailsVisibility = Visibility.Collapsed;
                row.Tag = null;
            }
        }
    }

    private string GetCommercialStatisticsGroupingTag() =>
        (StatisticsGroupCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant()
        ?? "partner";

    private string GetCommercialStatisticsGroupDisplayLabel(WpfCommercialStatisticsGroup group)
    {
        var groupBy = GetCommercialStatisticsGroupingTag();
        if (string.Equals(groupBy, "partner", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(group.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partnerId))
        {
            var partner = _partners.Select(row => row.Partner).FirstOrDefault(value => value.Id == partnerId);
            if (partner is not null && !string.IsNullOrWhiteSpace(partner.Name))
            {
                return partner.Name.Trim();
            }
        }

        if (string.Equals(groupBy, "item", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(group.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
        {
            var item = _items.FirstOrDefault(value => value.Id == itemId);
            if (item is not null)
            {
                return CommercialStatisticsDrillDown.FormatItemLabel(item.Gtin, item.Name, group.Label);
            }
        }

        return group.Label;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed class CommercialStatisticsGroupDisplayLabelConverter(MainWindow owner) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is WpfCommercialStatisticsGroup group
                ? owner.GetCommercialStatisticsGroupDisplayLabel(group)
                : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class CommercialStatisticsRowDetailsArrowConverter : IValueConverter
    {
        public static CommercialStatisticsRowDetailsArrowConverter Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility visibility && visibility == Visibility.Visible
                ? "▼"
                : "▶";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
