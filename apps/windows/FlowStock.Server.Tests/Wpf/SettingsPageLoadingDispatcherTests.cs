using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class SettingsPageLoadingDispatcherTests
{
    [Fact]
    public async Task Loaded_reentry_keeps_dispatcher_responsive_and_applies_data_on_UI_thread()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new TaskCompletionSource<(int Calls, bool Enabled, int Thread)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<(bool Enabled, int AppliedThread, int Thread)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var content = new Grid();
                var loading = new SettingsPageLoading(content, new FileLogger(Path.Combine(Path.GetTempPath(), "unused-settings-test.log")));
                var calls = 0;
                var appliedThread = 0;
                loading.InitializeOnLoaded(async () =>
                {
                    await loading.RunAsync(async () =>
                    {
                        calls++;
                        await release.Task;
                        appliedThread = Environment.CurrentManagedThreadId;
                        content.Tag = "loaded";
                    });
                    finished.TrySetResult((content.IsEnabled, appliedThread, Environment.CurrentManagedThreadId));
                });
                content.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                content.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                content.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                dispatcher.BeginInvoke(new Action(() =>
                    heartbeat.TrySetResult((calls, content.IsEnabled, Environment.CurrentManagedThreadId))));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                heartbeat.TrySetException(exception);
                finished.TrySetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var busy = await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, busy.Calls);
            Assert.False(busy.Enabled);
            Assert.False(finished.Task.IsCompleted); // dispatcher heartbeat ran while I/O was pending
            release.SetResult();
            var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(done.Enabled);
            Assert.Equal(busy.Thread, done.Thread);
            Assert.Equal(busy.Thread, done.AppliedThread);
        }
        finally
        {
            release.TrySetResult();
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        }
    }
}
