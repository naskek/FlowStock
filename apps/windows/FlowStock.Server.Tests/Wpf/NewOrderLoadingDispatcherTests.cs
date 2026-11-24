using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowStock.App;
using FlowStock.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace FlowStock.Server.Tests.Wpf;

[Collection("New order dispatcher")]
public sealed class NewOrderLoadingDispatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowReferences_KeepDispatcherResponsive_WithoutFullModelReads(bool closeDuringLoad)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new System.Collections.Concurrent.ConcurrentBag<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapGet("/api/partners", async () =>
        {
            requests.Add("partners");
            entered.TrySetResult();
            await release.Task;
            return Array.Empty<object>();
        });
        api.MapGet("/api/orders/next-ref", () =>
        {
            requests.Add("next-ref");
            return new { order_ref = "423" };
        });
        await api.StartAsync();
        var url = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var dir = Path.Combine(Path.GetTempPath(), "flowstock-new-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settingsPath = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            server = new { base_url = url, close_timeout_seconds = 10 }
        }));
        var logger = new FileLogger(Path.Combine(dir, "app.log"));
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var ready = new TaskCompletionSource<(OrderDetailsWindow Window, Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var services = (AppServices)typeof(AppServices).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(
                    [store.Object, "", "test", dir, dir, dir, settingsPath, Path.Combine(dir, "admin.json"),
                        Path.Combine(dir, "partners.json"), logger, logger, false, false, null]);
                var window = new OrderDetailsWindow(services);
                ready.TrySetResult((window, dispatcher));
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Dispatcher? ui = null;
        try
        {
            var (window, dispatcher) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ui = dispatcher;
            Assert.Empty(requests); // constructor must not perform I/O
            await ui.InvokeAsync(() =>
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.InvokeAsync(() =>
            {
                Assert.False(((UIElement)window.Content).IsEnabled);
                Assert.Equal("", ((TextBox)window.FindName("OrderRefBox")).Text);
                if (closeDuringLoad) window.Close();
            }); // heartbeat completes while the HTTP request is deliberately blocked
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!LoadCompleted())
                await Task.Delay(20, timeout.Token);
            await ui.InvokeAsync(() =>
            {
                Assert.Equal(closeDuringLoad ? "" : "423", ((TextBox)window.FindName("OrderRefBox")).Text);
                if (!closeDuringLoad)
                {
                    Assert.True(((UIElement)window.Content).IsEnabled);
                    window.Close();
                }
            });
            Assert.Equal(new[] { "next-ref", "partners" }, requests.OrderBy(x => x));
            store.VerifyNoOtherCalls(); // no items, orders, order lines, stock or DB fallback

            bool LoadCompleted()
            {
                var path = Path.Combine(dir, "app.log");
                if (!File.Exists(path)) return false;
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                return reader.ReadToEnd().Contains("new_order_load completed_ms=");
            }
        }
        finally
        {
            release.TrySetResult();
            ui?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            await api.StopAsync();
            Directory.Delete(dir, recursive: true);
        }
    }
}

[CollectionDefinition("New order dispatcher", DisableParallelization = true)]
public sealed class NewOrderDispatcherCollection;
