using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsViewStateTests
{
    [Fact]
    public void Pagination_and_detail_month_are_reflected_in_requests()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        var filters = CreateFilters();

        var first = state.StartLoad(filters);
        Assert.Equal(0, first.Request.Offset);
        Assert.Null(first.Request.DetailMonth);
        Assert.True(state.TryComplete(first.RequestId, Result(totalCount: 250, offset: 0, count: 100)));
        Assert.Equal("1–100 из 250", state.RangeText);

        Assert.True(state.MoveNext());
        var second = state.StartLoad(filters);
        Assert.Equal(100, second.Request.Offset);

        state.SelectDetailMonth("2026-02");
        var detail = state.StartLoad(filters);
        Assert.Equal(0, detail.Request.Offset);
        Assert.Equal("2026-02", detail.Request.DetailMonth);
        Assert.Equal("Детализация за февраль 2026", state.DetailLabel);

        state.SelectDetailMonth(null);
        Assert.Equal("Детализация за весь период", state.DetailLabel);
    }

    [Fact]
    public void Stale_response_cannot_replace_newer_page_state()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        var first = state.StartLoad(CreateFilters());
        var second = state.StartLoad(CreateFilters());

        Assert.False(state.TryComplete(
            first.RequestId,
            Result(totalCount: 900, offset: 0, count: 100)));
        Assert.True(state.TryComplete(
            second.RequestId,
            Result(totalCount: 25, offset: 0, count: 25)));
        Assert.Equal("1–25 из 25", state.RangeText);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public void Filter_change_resets_current_offset()
    {
        var state = new CommercialStatisticsViewState(pageSize: 100);
        var load = state.StartLoad(CreateFilters());
        Assert.True(state.TryComplete(
            load.RequestId,
            Result(totalCount: 250, offset: 0, count: 100)));
        Assert.True(state.MoveNext());

        state.ResetOffset();

        Assert.Equal(0, state.StartLoad(CreateFilters()).Request.Offset);
    }

    private static WpfCommercialStatisticsFilters CreateFilters() =>
        new(
            Mode: "orders",
            GroupBy: "partner",
            From: new DateTime(2026, 1, 1),
            To: new DateTime(2026, 3, 31),
            PartnerId: 10,
            ItemId: null,
            Gtin: null,
            Brand: null,
            Volume: null,
            Statuses: "ACCEPTED,IN_PROGRESS",
            Sort: "gross_desc");

    private static WpfCommercialStatisticsResult Result(
        int totalCount,
        int offset,
        int count) =>
        new()
        {
            Groups = new WpfCommercialStatisticsGroups
            {
                TotalCount = totalCount,
                Offset = offset,
                Limit = 100,
                Items = Enumerable.Range(1, count)
                    .Select(index => new WpfCommercialStatisticsGroup
                    {
                        Key = index.ToString(),
                        Label = $"Группа {index}"
                    })
                    .ToList()
            }
        };
}
