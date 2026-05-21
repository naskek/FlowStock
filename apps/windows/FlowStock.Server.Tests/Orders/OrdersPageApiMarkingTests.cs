using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrdersPageApiMarkingTests
{
    [Fact]
    public async Task OrdersPage_InternalMarkableOrderWithoutExport_ReturnsNotConducted()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderReadModelStore>();
        store.Setup(data => data.GetOrdersPage(true, null, 15, 0, false))
            .Returns([
                new Order
                {
                    Id = 77,
                    OrderRef = "077",
                    Type = OrderType.Internal,
                    Status = OrderStatus.InProgress,
                    CreatedAt = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
                    MarkingStatus = MarkingStatus.NotRequired,
                    MarkingRequired = true,
                    MarkingApplies = true,
                    MarkingCodeCovered = false,
                    ListMetricsLoaded = true
                }
            ]);

        await using var host = await OrdersPageHost.StartAsync(store.Object);

        using var response = await host.Client.GetAsync("/api/orders?include_internal=1&limit=15&offset=0&include_cancelled_merged=0");
        response.EnsureSuccessStatusCode();
        var rows = await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
        var row = rows.EnumerateArray().Single();

        Assert.Equal(77, row.GetProperty("id").GetInt64());
        Assert.True(row.GetProperty("marking_required").GetBoolean());
        Assert.False(row.GetProperty("marking_completed").GetBoolean());
        Assert.NotEqual("PRINTED", row.GetProperty("marking_status").GetString());
        Assert.Equal("REQUIRED", row.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка не проведена", row.GetProperty("marking_label").GetString());
        Assert.Equal("Маркировка не проведена", row.GetProperty("marking_status_display").GetString());
        Assert.Equal(string.Empty, row.GetProperty("marking_excel_generated_at").GetString() ?? string.Empty);
        Assert.Equal(string.Empty, row.GetProperty("marking_printed_at").GetString() ?? string.Empty);
    }

    private sealed class OrdersPageHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private OrdersPageHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<OrdersPageHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderApiMapper).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);

            var app = builder.Build();
            app.MapGet("/api/orders", (HttpRequest request, IDataStore dataStore) =>
            {
                var includeInternal = string.Equals(request.Query["include_internal"], "1", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(request.Query["include_internal"], "true", StringComparison.OrdinalIgnoreCase);
                var includeCancelledMerged = string.Equals(request.Query["include_cancelled_merged"], "1", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(request.Query["include_cancelled_merged"], "true", StringComparison.OrdinalIgnoreCase);
                var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? parsedLimit : 15;
                var offset = int.TryParse(request.Query["offset"], out var parsedOffset) ? parsedOffset : 0;

                var rows = new OrderService(dataStore)
                    .GetOrdersPage(includeInternal, null, limit, offset, includeCancelledMerged)
                    .Select(order => OrderApiMapper.MapOrder(
                        order,
                        order.HasShipmentRemaining,
                        order.HasProductionPalletPlan,
                        order.NeedsProductionPalletPlan,
                        new ProductionPalletSummary
                        {
                            PlannedPalletCount = order.PlannedPalletCount,
                            FilledPalletCount = order.FilledPalletCount,
                            PlannedQty = order.PlannedQty,
                            FilledQty = order.FilledQty
                        }))
                    .ToArray();

                return Results.Ok(rows);
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException("HTTP test host did not expose a listening address.");
            }

            return new OrdersPageHost(app, new HttpClient { BaseAddress = new Uri(address, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
