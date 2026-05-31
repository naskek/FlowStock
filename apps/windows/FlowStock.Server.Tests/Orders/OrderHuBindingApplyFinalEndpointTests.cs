using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderHuBindingApplyFinalEndpointTests
{
    [Fact]
    public async Task ApplyFinalEndpoint_RequiresReplaceFinalSelectionMode()
    {
        var harness = CreateHarness();
        await using var host = await ApplyFinalHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/hu-bindings/apply-final",
            new
            {
                lines = new[]
                {
                    new
                    {
                        order_line_id = 101L,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_REQUEST", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApplyFinalEndpoint_RequiresExpectedBoundHuCodes()
    {
        var harness = CreateHarness();
        await using var host = await ApplyFinalHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/hu-bindings/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_line_id = 101L,
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_REQUEST", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApplyFinalEndpoint_EmptyFinalHuCodesMeansExplicitDetach()
    {
        var harness = CreateHarness();
        harness.SeedOrderReceiptPlanLines(
            10,
            new OrderReceiptPlanLine
            {
                OrderId = 10,
                OrderLineId = 101,
                ItemId = 6,
                QtyPlanned = 600,
                ToHu = "HU-OLD"
            });
        await using var host = await ApplyFinalHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/hu-bindings/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_line_id = 101L,
                        expected_bound_hu_codes = new[] { "hu-old" },
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        var line = document.RootElement.GetProperty("applied_lines").EnumerateArray().Single();
        Assert.Equal("HU-OLD", line.GetProperty("detached_hu_codes").EnumerateArray().Single().GetString());
        Assert.DoesNotContain(harness.GetOrderReceiptPlanLines(10), planLine => planLine.OrderLineId == 101);
    }

    [Fact]
    public async Task ApplyFinalEndpoint_ReturnsHuBindingStale()
    {
        var harness = CreateHarness();
        harness.SeedOrderReceiptPlanLines(
            10,
            new OrderReceiptPlanLine
            {
                OrderId = 10,
                OrderLineId = 101,
                ItemId = 6,
                QtyPlanned = 600,
                ToHu = "HU-OLD"
            });
        await using var host = await ApplyFinalHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/hu-bindings/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_line_id = 101L,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("HU_BINDING_STALE", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LegacyHuReservationsApplyEndpoint_SemanticsUnchanged()
    {
        var harness = CreateHarness();
        harness.SeedBalance(6, 1, 600, "HU-READY");
        await using var host = await ApplyFinalHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/hu-reservations/apply",
            new
            {
                lines = new[]
                {
                    new
                    {
                        order_line_id = 101L,
                        selected_hu_codes = new[] { "HU-READY" }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        var line = document.RootElement.GetProperty("applied_lines").EnumerateArray().Single();
        Assert.Equal(600, line.GetProperty("reserved_qty").GetDouble(), 3);
        Assert.False(line.TryGetProperty("final_hu_codes", out _));
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item
        {
            Id = 6,
            Name = "Товар",
            BaseUom = "шт",
            ItemTypeId = 1,
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerId = 1,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 6,
            QtyOrdered = 600
        });
        return harness;
    }

    private sealed class ApplyFinalHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ApplyFinalHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<ApplyFinalHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderHuBindingApplyFinalEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            OrderHuReservationApplyEndpoint.Map(app);
            OrderHuBindingApplyFinalEndpoint.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new ApplyFinalHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
