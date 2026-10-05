using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FlowStock.App;

internal static class CommercialStatisticsDrillDown
{
    public const int PageSize = 500;

    public static WpfCommercialStatisticsRequest BuildPartnerItemsRequest(
        WpfCommercialStatisticsFilters filters,
        CommercialStatisticsViewState state,
        long partnerId,
        int offset = 0)
    {
        var from = state.DetailPeriodFrom ?? filters.From;
        var to = state.DetailPeriodTo ?? filters.To;
        return new WpfCommercialStatisticsRequest(
            filters.Mode,
            "item",
            from,
            to,
            DetailMonth: state.DetailMonth,
            PartnerId: partnerId,
            ItemId: filters.ItemId,
            Gtin: filters.Gtin,
            Brand: filters.Brand,
            Volume: filters.Volume,
            Statuses: filters.Statuses,
            Limit: PageSize,
            Offset: Math.Max(0, offset),
            Sort: "name_asc",
            Gtins: state.Gtins,
            ItemNameContains: state.ItemNameContains,
            Volumes: state.Volumes);
    }

    public static IReadOnlyList<CommercialStatisticsDrillDownSection> GroupByVolume(
        IEnumerable<WpfCommercialStatisticsGroup> groups,
        IReadOnlyDictionary<long, CommercialStatisticsDrillDownItemCatalogEntry> catalog)
    {
        var rows = groups.Select(group =>
        {
            catalog.TryGetValue(ParsePositiveId(group.Key), out var item);
            var volume = string.IsNullOrWhiteSpace(item?.Volume)
                ? "Без фасовки"
                : item.Volume!.Trim();
            var label = FormatItemLabel(item?.Gtin, item?.Name, group.Label);
            return new CommercialStatisticsDrillDownItem(label, group.Amounts, volume);
        });

        return rows
            .GroupBy(row => row.Volume, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => string.Equals(group.Key, "Без фасовки", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new CommercialStatisticsDrillDownSection(
                group.Key,
                group.OrderBy(row => row.Label, StringComparer.CurrentCultureIgnoreCase).ToArray()))
            .ToArray();
    }

    public static string FormatItemLabel(string? gtin, string? name, string? fallback = null)
    {
        var normalizedGtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim();
        var normalizedName = !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : !string.IsNullOrWhiteSpace(fallback)
                ? fallback.Trim()
                : "Не указано";
        return normalizedGtin is null
            ? normalizedName
            : $"{normalizedGtin} — {normalizedName}";
    }

    public static bool AmountsMatch(
        WpfCommercialStatisticsAmounts parent,
        IEnumerable<WpfCommercialStatisticsGroup> children)
    {
        var rows = children.ToArray();
        return parent.Quantity == rows.Sum(row => row.Amounts.Quantity)
               && parent.Gross == rows.Sum(row => row.Amounts.Gross)
               && parent.Net == rows.Sum(row => row.Amounts.Net)
               && parent.Vat == rows.Sum(row => row.Amounts.Vat);
    }

    private static long ParsePositiveId(string? value) =>
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;
}

internal sealed record CommercialStatisticsDrillDownItemCatalogEntry(
    long Id,
    string Name,
    string? Gtin,
    string? Volume);

internal sealed record CommercialStatisticsDrillDownItem(
    string Label,
    WpfCommercialStatisticsAmounts Amounts,
    string Volume);

internal sealed record CommercialStatisticsDrillDownSection(
    string Volume,
    IReadOnlyList<CommercialStatisticsDrillDownItem> Items);

internal sealed class CommercialStatisticsPartnerDrillDownState : INotifyPropertyChanged
{
    private string _statusText = "Загрузка...";
    private Visibility _statusVisibility = Visibility.Visible;

    public ObservableCollection<CommercialStatisticsDrillDownSection> Sections { get; } = [];
    public bool IsLoading { get; private set; }
    public bool IsLoaded { get; private set; }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }
            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public Visibility StatusVisibility
    {
        get => _statusVisibility;
        private set
        {
            if (_statusVisibility == value)
            {
                return;
            }
            _statusVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusVisibility)));
        }
    }

    public void BeginLoading()
    {
        IsLoading = true;
        IsLoaded = false;
        Sections.Clear();
        StatusText = "Загрузка товарного состава...";
        StatusVisibility = Visibility.Visible;
    }

    public void Complete(IEnumerable<CommercialStatisticsDrillDownSection> sections, string? warning = null)
    {
        Sections.Clear();
        foreach (var section in sections)
        {
            Sections.Add(section);
        }
        IsLoading = false;
        IsLoaded = true;
        if (!string.IsNullOrWhiteSpace(warning))
        {
            StatusText = warning;
            StatusVisibility = Visibility.Visible;
        }
        else if (Sections.Count == 0)
        {
            StatusText = "Товары не найдены.";
            StatusVisibility = Visibility.Visible;
        }
        else
        {
            StatusText = string.Empty;
            StatusVisibility = Visibility.Collapsed;
        }
    }

    public void Fail(string message)
    {
        IsLoading = false;
        IsLoaded = false;
        Sections.Clear();
        StatusText = string.IsNullOrWhiteSpace(message)
            ? "Не удалось загрузить товарный состав."
            : message;
        StatusVisibility = Visibility.Visible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class CommercialStatisticsPartnerDrillDownView : Border
{
    public CommercialStatisticsPartnerDrillDownView()
    {
        Margin = new Thickness(24, 4, 6, 8);
        Padding = new Thickness(10, 8, 10, 8);
        BorderThickness = new Thickness(1);
        BorderBrush = System.Windows.Media.Brushes.LightGray;
        Background = System.Windows.Media.Brushes.WhiteSmoke;

        var panel = new StackPanel();
        Child = panel;

        var status = new TextBlock
        {
            Margin = new Thickness(2, 0, 2, 6),
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(CommercialStatisticsPartnerDrillDownState.StatusText)));
        status.SetBinding(VisibilityProperty, new Binding(nameof(CommercialStatisticsPartnerDrillDownState.StatusVisibility)));
        panel.Children.Add(status);

        var sections = new ItemsControl();
        sections.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(CommercialStatisticsPartnerDrillDownState.Sections)));
        sections.ItemTemplate = BuildSectionTemplate();
        panel.Children.Add(sections);
    }

    private static DataTemplate BuildSectionTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.MarginProperty, new Thickness(0, 2, 0, 8));

        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(CommercialStatisticsDrillDownSection.Volume)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 2, 4));
        root.AppendChild(title);

        var grid = new FrameworkElementFactory(typeof(CommercialStatisticsDrillDownItemsGrid));
        grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(CommercialStatisticsDrillDownSection.Items)));
        root.AppendChild(grid);

        return new DataTemplate
        {
            VisualTree = root
        };
    }
}

internal sealed class CommercialStatisticsDrillDownItemsGrid : DataGrid
{
    public CommercialStatisticsDrillDownItemsGrid()
    {
        AutoGenerateColumns = false;
        IsReadOnly = true;
        CanUserAddRows = false;
        CanUserDeleteRows = false;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        Margin = new Thickness(0);
        MaxHeight = 320;

        Columns.Add(new DataGridTextColumn
        {
            Header = "Товар / GTIN",
            Binding = new Binding(nameof(CommercialStatisticsDrillDownItem.Label)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        Columns.Add(new DataGridTextColumn
        {
            Header = "Количество",
            Binding = new Binding("Amounts.Quantity") { StringFormat = "{0:0.######}" },
            Width = 95
        });
        Columns.Add(new DataGridTextColumn
        {
            Header = "С НДС",
            Binding = new Binding("Amounts.Gross") { StringFormat = "{0:N2}" },
            Width = 105
        });
        Columns.Add(new DataGridTextColumn
        {
            Header = "Без НДС",
            Binding = new Binding("Amounts.Net") { StringFormat = "{0:N2}" },
            Width = 105
        });
        Columns.Add(new DataGridTextColumn
        {
            Header = "НДС",
            Binding = new Binding("Amounts.Vat") { StringFormat = "{0:N2}" },
            Width = 90
        });
    }
}
