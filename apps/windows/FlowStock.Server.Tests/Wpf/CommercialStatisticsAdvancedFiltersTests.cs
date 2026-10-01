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
}
