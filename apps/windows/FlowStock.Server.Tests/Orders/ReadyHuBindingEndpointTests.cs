using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.Orders;

public sealed class ReadyHuBindingEndpointTests
{
    [Fact]
    public async Task ReadyHuBindingEndpoint_ReturnsComputedReadModel()
    {
        var harness = CreateHarnessWithReadyHu();
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/orders/hu-bindings/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("READY_HU_BINDING_AVAILABLE", document.RootElement.GetProperty("request_type").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("hu_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("order_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("line_count").GetInt32());
        Assert.Equal("HU-READY", document.RootElement.GetProperty("hu_rows").EnumerateArray().Single().GetProperty("hu_code").GetString());
    }

    [Fact]
    public async Task RequestsSummary_IncludesReadyHuBindingPending()
    {
        var harness = CreateHarnessWithReadyHu();
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/requests/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(1, document.RootElement.GetProperty("ready_hu_binding_pending").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("total_pending").GetInt32());
    }

    [Fact]
    public async Task RequestsSummary_ReadyHuBindingPendingIsZeroWhenNoCompatibleFreeHuExists()
    {
        var harness = CreateHarness();
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/requests/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(0, document.RootElement.GetProperty("ready_hu_binding_pending").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("total_pending").GetInt32());
    }

    [Fact]
    public void ProgramRequestsSummary_UsesReadyHuBindingPendingField()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Server", "Program.cs");

        Assert.Contains("ready_hu_binding_pending", source);
        Assert.Contains("ReadyHuBindingEndpoint.CountPendingNotifications(store)", source);
        Assert.Contains("total_pending = itemCount + orderCount + readyHuBindingPending", source);
    }

    [Fact]
    public void ReadyHuBindingReadModel_ReusesCandidatesServiceAndDoesNotWriteState()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Core", "Services", "ReadyHuBindingReadModelService.cs");

        Assert.Contains("new HuReservationCandidatesService(_dataStore).Build", source);
        Assert.Contains("IOptimizedHuReservationCandidatesStore", source);
        Assert.Contains("GetReservedOrderReceiptHuCodes(null)", source);
        Assert.DoesNotContain("ReplaceOrderReceiptPlanLines", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddLedgerEntry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelProductionPallet", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateOrder(", source, StringComparison.Ordinal);
    }

    private static CloseDocumentHarness CreateHarnessWithReadyHu()
    {
        var harness = CreateHarness();
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = ItemId,
            QtyOrdered = 600
        });
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");
        return harness;
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = LocationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = ItemId, Name = "Товар", BaseUom = "шт" });
        return harness;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private sealed class ReadyHuBindingHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ReadyHuBindingHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<ReadyHuBindingHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ReadyHuBindingEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            ReadyHuBindingEndpoint.Map(app);
            app.MapGet("/api/requests/summary", (IDataStore dataStore) =>
            {
                var itemCount = dataStore.GetItemRequests(false).Count;
                var orderCount = dataStore.GetOrderRequests(false).Count;
                var readyHuBindingPending = ReadyHuBindingEndpoint.CountPendingNotifications(dataStore);
                return Results.Ok(new
                {
                    item_requests_pending = itemCount,
                    order_requests_pending = orderCount,
                    ready_hu_binding_pending = readyHuBindingPending,
                    total_pending = itemCount + orderCount + readyHuBindingPending
                });
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new ReadyHuBindingHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private const long ItemId = 6;
    private const long LocationId = 1;
}
