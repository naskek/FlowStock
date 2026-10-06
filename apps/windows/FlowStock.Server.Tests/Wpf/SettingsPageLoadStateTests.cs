using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class SettingsPageLoadStateTests
{
    [Fact]
    public async Task Slow_initial_load_returns_immediately_and_reentry_shares_one_task()
    {
        var state = new SettingsPageLoadState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var applied = false;
        Task Load() => state.RunAsync(async () =>
        {
            calls++;
            await release.Task;
            applied = true;
        });

        var first = state.InitializeAsync(Load);
        Assert.False(first.IsCompleted);
        Assert.False(applied);
        Assert.Same(first, state.InitializeAsync(Load)); // navigate away/back while loading
        Assert.Same(first, state.RunAsync(Load)); // repeated refresh while loading
        Assert.Equal(1, calls);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(applied);
        Assert.Same(first, state.InitializeAsync(Load)); // cached page, no repeated I/O
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Different_pages_can_load_without_blocking_navigation()
    {
        var reasons = new SettingsPageLoadState();
        var uoms = new SettingsPageLoadState();
        var releaseReasons = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUoms = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = reasons.InitializeAsync(() => reasons.RunAsync(() => releaseReasons.Task));
        var second = uoms.InitializeAsync(() => uoms.RunAsync(() => releaseUoms.Task));

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        releaseUoms.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        releaseReasons.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Explicit_reload_after_mutation_completes_before_callback_and_runs_again()
    {
        var state = new SettingsPageLoadState();
        var order = new List<string>();
        await state.InitializeAsync(() => state.RunAsync(() => Task.CompletedTask));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = state.RunAsync(async () =>
        {
            await release.Task;
            order.Add("reload");
        });
        var callback = AfterReload();
        Assert.Empty(order);
        release.SetResult();
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "reload", "callback" }, order);
        await state.RunAsync(() =>
        {
            order.Add("next reload");
            return Task.CompletedTask;
        });
        Assert.Equal(3, order.Count);

        async Task AfterReload()
        {
            await reload;
            order.Add("callback");
        }
    }

    [Fact]
    public async Task Failed_initialization_can_be_retried_without_poisoning_gate()
    {
        var state = new SettingsPageLoadState();
        await Assert.ThrowsAsync<IOException>(() => state.InitializeAsync(() =>
            state.RunAsync(() => throw new IOException("read failed"))));
        await state.InitializeAsync(() => state.RunAsync(() => Task.CompletedTask));
        await state.RunAsync(() => Task.CompletedTask);
    }
}
