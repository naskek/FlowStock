using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsAcceptanceBehaviorTests
{
    [Fact]
    public void Volume_master_reselects_every_specific_volume_from_subset()
    {
        var options = CommercialStatisticsVolumeFilterOptions.Build(
            new[]
            {
                new CommercialStatisticsTextFilterOption(null, "Все фасовки"),
                new CommercialStatisticsTextFilterOption("190 мл", "190 мл"),
                new CommercialStatisticsTextFilterOption("500 мл", "500 мл"),
                new CommercialStatisticsTextFilterOption("1000 мл", "1000 мл")
            });

        options.Single(option => option.Value == "500 мл").IsChecked = false;
        Assert.False(options.Single(option => option.IsAll).IsChecked);

        options.Single(option => option.IsAll).IsChecked = true;

        Assert.All(options, option => Assert.True(option.IsChecked));
        Assert.Null(CommercialStatisticsVolumeFilterOptions.BuildCsv(options));
        Assert.Equal("Все фасовки", CommercialStatisticsVolumeFilterOptions.BuildLabel(options));
    }

    [Fact]
    public void Detail_month_keeps_original_navigation_period_across_filter_refreshes()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        var navigationFrom = new DateTime(2026, 1, 1);
        var navigationTo = new DateTime(2026, 9, 30);
        var visibleMonthFrom = new DateTime(2026, 9, 1);
        var visibleMonthTo = new DateTime(2026, 9, 30);

        state.SelectDetailMonth("2026-09", navigationFrom, navigationTo);
        var first = state.StartLoad(CreateFilters(visibleMonthFrom, visibleMonthTo));

        Assert.Equal(navigationFrom, first.Request.From);
        Assert.Equal(navigationTo, first.Request.To);
        Assert.Equal("2026-09", first.Request.DetailMonth);
        Assert.Equal(navigationFrom, state.DetailPeriodFrom);
        Assert.Equal(navigationTo, state.DetailPeriodTo);

        state.CriteriaChanged(periodChanged: false);
        var filtered = state.StartLoad(
            CreateFilters(visibleMonthFrom, visibleMonthTo) with { Brand = "TEST-BRAND" });

        Assert.Equal(navigationFrom, filtered.Request.From);
        Assert.Equal(navigationTo, filtered.Request.To);
        Assert.Equal("2026-09", filtered.Request.DetailMonth);
        Assert.Equal("TEST-BRAND", filtered.Request.Brand);
    }

    [Fact]
    public void Commercial_statistics_default_mode_is_sales()
    {
        Assert.Equal("sales", CommercialStatisticsModeDefaultsPolicy.DefaultMode);
    }

    [Fact]
    public void Orders_default_scope_restores_active_and_shipped_statuses()
    {
        var statuses = CommercialStatisticsFilterOptions.BuildStatuses();
        foreach (var option in statuses)
        {
            option.IsChecked = string.Equals(option.Code, "SHIPPED", StringComparison.OrdinalIgnoreCase);
        }

        CommercialStatisticsModeDefaultsPolicy.ApplyDefaultOrderStatuses(statuses);

        Assert.Equal(
            new[] { "DRAFT", "ACCEPTED", "IN_PROGRESS", "SHIPPED" },
            statuses.Where(option => option.IsChecked).Select(option => option.Code).ToArray());
        Assert.Null(CommercialStatisticsModeDefaultsPolicy.BuildStatusesCsv("orders", statuses));
        Assert.Null(CommercialStatisticsModeDefaultsPolicy.BuildStatusesCsv("sales", statuses));
        Assert.Equal("Все статусы", CommercialStatisticsFilterOptions.BuildStatusesLabel(statuses));
    }

    [Fact]
    public void Partner_scope_uses_current_orders_status_selection()
    {
        var statuses = CommercialStatisticsFilterOptions.BuildStatuses();
        statuses.Single(option => option.Code == "DRAFT").IsChecked = false;
        statuses.Single(option => option.Code == "ACCEPTED").IsChecked = false;

        Assert.Equal(
            "IN_PROGRESS,SHIPPED",
            CommercialStatisticsModeDefaultsPolicy.BuildStatusesCsv("orders", statuses));
        Assert.Null(CommercialStatisticsModeDefaultsPolicy.BuildStatusesCsv("sales", statuses));
    }

    [Fact]
    public void Orders_status_filter_can_still_be_narrowed_to_shipped_only()
    {
        var statuses = CommercialStatisticsFilterOptions.BuildStatuses();
        foreach (var option in statuses)
        {
            option.IsChecked = string.Equals(option.Code, "SHIPPED", StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            "SHIPPED",
            CommercialStatisticsFilterOptions.BuildStatusesCsv("orders", statuses));
        Assert.Null(CommercialStatisticsFilterOptions.BuildStatusesCsv("sales", statuses));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("горчица", false)]
    public void Empty_selector_text_is_restored_to_all_on_blur(string? text, bool expected)
    {
        Assert.Equal(expected, CommercialStatisticsSelectorBlurPolicy.ShouldRestoreAll(text));
    }

    private static WpfCommercialStatisticsFilters CreateFilters(DateTime from, DateTime to) =>
        new(
            Mode: "orders",
            GroupBy: "item",
            From: from,
            To: to,
            PartnerId: null,
            ItemId: null,
            Gtin: null,
            Brand: null,
            Volume: null,
            Statuses: "SHIPPED",
            Sort: "gross_desc");
}
