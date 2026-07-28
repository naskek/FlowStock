using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsAutoRefreshCoordinatorTests
{
    [Fact]
    public void Rapid_schedules_are_consumed_once_at_latest_revision()
    {
        var coordinator = new CommercialStatisticsAutoRefreshCoordinator();

        coordinator.Schedule();
        coordinator.Schedule();
        var latestRevision = coordinator.Schedule();

        Assert.True(coordinator.TryConsume(out var consumedRevision));
        Assert.Equal(latestRevision, consumedRevision);
        Assert.False(coordinator.TryConsume(out _));
    }

    [Fact]
    public void Month_or_whole_period_action_can_cancel_pending_debounced_refresh()
    {
        var coordinator = new CommercialStatisticsAutoRefreshCoordinator();
        coordinator.Schedule();

        coordinator.Cancel();

        Assert.False(coordinator.TryConsume(out _));
    }
}
