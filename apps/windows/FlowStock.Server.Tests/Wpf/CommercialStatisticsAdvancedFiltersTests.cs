using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsAdvancedFiltersTests
{
    [Fact]
    public void Item_name_filter_collapses_whitespace_and_preserves_literal_text()
    {
        Assert.Equal(
            "аджика острая",
            CommercialStatisticsAdvancedFilters.NormalizeItemNameContains("  аджика   острая  "));
        Assert.Equal(
            "100%_аджика",
            CommercialStatisticsAdvancedFilters.NormalizeItemNameContains("100%_аджика"));
        Assert.Null(CommercialStatisticsAdvancedFilters.NormalizeItemNameContains("   "));
    }

    [Fact]
    public void Gtin_list_normalizes_whitespace_separators_duplicates_and_empty_values()
    {
        var result = CommercialStatisticsAdvancedFilters.NormalizeGtinsCsv(
            " 0460 111, 0460222 ; 0460 111\r\n0460333 ,, ");

        Assert.Equal("0460111,0460222,0460333", result);
        Assert.Null(CommercialStatisticsAdvancedFilters.NormalizeGtinsCsv(" , ; \r\n "));
    }

    [Theory]
    [InlineData(2026, 9, 1, 30)]
    [InlineData(2024, 2, 1, 29)]
    [InlineData(2026, 12, 1, 31)]
    public void Month_period_uses_full_calendar_month(
        int year,
        int month,
        int expectedStartDay,
        int expectedEndDay)
    {
        var period = CommercialStatisticsAdvancedFilters.MonthPeriod(
            new DateTime(year, month, 17));

        Assert.Equal(new DateTime(year, month, expectedStartDay), period.From);
        Assert.Equal(new DateTime(year, month, expectedEndDay), period.To);
    }

    [Fact]
    public void View_state_forwards_advanced_filters_without_client_post_filtering()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        var load = state.StartLoad(new WpfCommercialStatisticsFilters(
            Mode: "sales",
            GroupBy: "gtin",
            From: new DateTime(2026, 9, 1),
            To: new DateTime(2026, 9, 30),
            PartnerId: null,
            ItemId: null,
            Gtin: null,
            Brand: null,
            Volume: "190 мл",
            Statuses: null,
            Sort: "gross_desc",
            Gtins: "0460111,0460222",
            ItemNameContains: "аджика"));

        Assert.Equal("0460111,0460222", load.Request.Gtins);
        Assert.Equal("аджика", load.Request.ItemNameContains);
        Assert.Equal("190 мл", load.Request.Volume);
        Assert.Equal(0, load.Request.Offset);
        Assert.Equal(100, load.Request.Limit);
    }

    [Fact]
    public void View_state_retains_name_and_multi_volume_filters_when_legacy_snapshot_is_built()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        state.SetAdvancedFilters(
            gtins: null,
            itemNameContains: "аджика",
            volumes: "190 мл,500 мл");

        var load = state.StartLoad(new WpfCommercialStatisticsFilters(
            Mode: "sales",
            GroupBy: "item",
            From: new DateTime(2026, 9, 1),
            To: new DateTime(2026, 9, 30),
            PartnerId: null,
            ItemId: null,
            Gtin: null,
            Brand: null,
            Volume: null,
            Statuses: null,
            Sort: "gross_desc"));

        Assert.Null(load.Request.Gtins);
        Assert.Equal("аджика", load.Request.ItemNameContains);
        Assert.Equal("190 мл,500 мл", load.Request.Volumes);
        Assert.Null(load.Request.Volume);
    }

    [Fact]
    public void Volume_checklist_uses_null_for_all_and_csv_for_subset()
    {
        var options = CommercialStatisticsVolumeFilterOptions.Build(
            new[]
            {
                new CommercialStatisticsTextFilterOption(null, "Все фасовки"),
                new CommercialStatisticsTextFilterOption("190 мл", "190 мл"),
                new CommercialStatisticsTextFilterOption("500 мл", "500 мл"),
                new CommercialStatisticsTextFilterOption("1000 мл", "1000 мл")
            });

        Assert.Null(CommercialStatisticsVolumeFilterOptions.BuildCsv(options));
        Assert.Equal("Все фасовки", CommercialStatisticsVolumeFilterOptions.BuildLabel(options));

        options[1].IsChecked = false;
        Assert.Equal("190 мл,1000 мл", CommercialStatisticsVolumeFilterOptions.BuildCsv(options));
        Assert.Equal("190 мл, 1000 мл", CommercialStatisticsVolumeFilterOptions.BuildLabel(options));
    }

    [Fact]
    public void Volume_checklist_restores_all_when_last_checkbox_is_cleared()
    {
        var options = CommercialStatisticsVolumeFilterOptions.Build(
            new[]
            {
                new CommercialStatisticsTextFilterOption(null, "Все фасовки"),
                new CommercialStatisticsTextFilterOption("190 мл", "190 мл"),
                new CommercialStatisticsTextFilterOption("500 мл", "500 мл")
            });

        foreach (var option in options)
        {
            option.IsChecked = false;
        }

        Assert.Equal("Все фасовки", CommercialStatisticsVolumeFilterOptions.BuildLabel(options));
        Assert.All(options, option => Assert.True(option.IsChecked));
        Assert.Null(CommercialStatisticsVolumeFilterOptions.BuildCsv(options));
    }
}
