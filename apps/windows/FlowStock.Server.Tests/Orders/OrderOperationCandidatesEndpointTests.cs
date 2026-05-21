using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderOperationCandidatesEndpointTests
{
    [Fact]
    public async Task InvalidDocType_Returns400()
    {
        await using var host = await OrderOperationCandidatesHost.StartAsync(CreateStore().Object);

        using var response = await host.Client.GetAsync("/api/orders/candidates?doc_type=INBOUND&limit=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_DOC_TYPE", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Limit_IsClampedToFifty_ForOptimizedStore()
    {
        var store = CreateStore();
        var optimized = store.As<IOptimizedOperationOrderCandidatesStore>();
        optimized.Setup(data => data.GetOperationOrderCandidates(
                DocType.ProductionReceipt,
                null,
                50))
            .Returns(Array.Empty<Order>());

        await using var host = await OrderOperationCandidatesHost.StartAsync(store.Object);

        using var response = await host.Client.GetAsync("/api/orders/candidates?doc_type=PRODUCTION_RECEIPT&limit=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        optimized.Verify(
            data => data.GetOperationOrderCandidates(DocType.ProductionReceipt, null, 50),
            Times.Once);
    }

    [Fact]
    public async Task ProductionReceipt_ReturnsOnlyReceiptNeedCandidates_OnFallbackStore()
    {
        var store = CreateStore();
        store.Setup(data => data.GetOrdersPage(true, null, 50, 0, false))
            .Returns(new[]
            {
                CreateLoadedOrder(1, OrderType.Customer, OrderStatus.Accepted, needsProductionPalletPlan: true, hasShipmentRemaining: false),
                CreateLoadedOrder(2, OrderType.Customer, OrderStatus.Accepted, needsProductionPalletPlan: false, hasShipmentRemaining: true),
                CreateLoadedOrder(3, OrderType.Internal, OrderStatus.Accepted, needsProductionPalletPlan: false, hasShipmentRemaining: true)
            });

        await using var host = await OrderOperationCandidatesHost.StartAsync(store.Object);

        var rows = await ReadJsonArray(host.Client, "/api/orders/candidates?doc_type=PRODUCTION_RECEIPT&limit=50");

        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("id").GetInt64());
        Assert.True(rows[0].GetProperty("needs_production_pallet_plan").GetBoolean());
    }

    [Fact]
    public async Task Outbound_ReturnsOnlyCustomerOrdersWithShipmentRemaining_OnFallbackStore()
    {
        var store = CreateStore();
        store.Setup(data => data.GetOrdersPage(true, null, 50, 0, false))
            .Returns(new[]
            {
                CreateLoadedOrder(10, OrderType.Customer, OrderStatus.Accepted, needsProductionPalletPlan: false, hasShipmentRemaining: true),
                CreateLoadedOrder(11, OrderType.Customer, OrderStatus.Accepted, needsProductionPalletPlan: false, hasShipmentRemaining: false),
                CreateLoadedOrder(12, OrderType.Internal, OrderStatus.Accepted, needsProductionPalletPlan: false, hasShipmentRemaining: true)
            });

        await using var host = await OrderOperationCandidatesHost.StartAsync(store.Object);

        var rows = await ReadJsonArray(host.Client, "/api/orders/candidates?doc_type=OUTBOUND&limit=50");

        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(10, rows[0].GetProperty("id").GetInt64());
        Assert.True(rows[0].GetProperty("has_shipment_remaining").GetBoolean());
    }

    [Fact]
    public void PostgresDataStore_GetOperationOrderCandidates_FiltersByDocTypeInSql()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("public IReadOnlyList<Order> GetOperationOrderCandidates(DocType docType, string? query, int limit)", source, StringComparison.Ordinal);
        Assert.Contains("@doc_type = @production_receipt_doc_type", source, StringComparison.Ordinal);
        Assert.Contains("candidate_orders.has_receipt_remaining", source, StringComparison.Ordinal);
        Assert.Contains("@doc_type = @outbound_doc_type", source, StringComparison.Ordinal);
        Assert.Contains("candidate_orders.has_shipment_remaining", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(limit, 1, 50)", source, StringComparison.Ordinal);
    }

    private static Mock<IDataStore> CreateStore()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderReadModelStore>();
        return store;
    }

    private static Order CreateLoadedOrder(
        long id,
        OrderType type,
        OrderStatus status,
        bool needsProductionPalletPlan,
        bool hasShipmentRemaining)
    {
        return new Order
        {
            Id = id,
            OrderRef = id.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            Type = type,
            Status = status,
            NeedsProductionPalletPlan = needsProductionPalletPlan,
            HasShipmentRemaining = hasShipmentRemaining,
            HasProductionPalletPlan = needsProductionPalletPlan,
            ListMetricsLoaded = true,
            CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static async Task<JsonElement> ReadJsonArray(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static string GetPostgresDataStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("PostgresDataStore.cs not found from test output directory.");
    }

    private sealed class OrderOperationCandidatesHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private OrderOperationCandidatesHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<OrderOperationCandidatesHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderOperationCandidatesEndpointTests).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);

            var app = builder.Build();
            app.MapGet("/api/orders/candidates", (HttpRequest request, IDataStore dataStore) =>
            {
                var docType = DocTypeMapper.FromOpString(request.Query["doc_type"].ToString());
                if (docType is not (DocType.ProductionReceipt or DocType.Outbound))
                {
                    return Results.BadRequest(new ApiResult(false, "INVALID_DOC_TYPE"));
                }

                var query = request.Query["q"].ToString();
                var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
                var limit = Math.Clamp(ParseLimit(request.Query["limit"]), 1, OperationOrderCandidatesApiQuery.MaxLimit);
                IReadOnlyList<Order> orders;
                if (dataStore is IOptimizedOperationOrderCandidatesStore optimizedStore)
                {
                    orders = optimizedStore.GetOperationOrderCandidates(docType.Value, normalized, limit);
                }
                else
                {
                    orders = new OrderService(dataStore)
                        .GetOrdersPage(includeInternal: true, normalized, limit, 0)
                        .Where(order => OperationOrderCandidatePolicy.IsCandidate(order, docType.Value))
                        .ToList();
                }

                return Results.Ok(orders
                    .Select(order => OrderApiMapper.MapOrder(
                        order,
                        order.HasShipmentRemaining,
                        order.HasProductionPalletPlan,
                        order.NeedsProductionPalletPlan))
                    .ToList());
            });

            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(address))
            {
                await app.StopAsync();
                await app.DisposeAsync();
                throw new InvalidOperationException("HTTP test host did not expose a listening address.");
            }

            return new OrderOperationCandidatesHost(app, new HttpClient
            {
                BaseAddress = new Uri(address, UriKind.Absolute)
            });
        }

        private static int ParseLimit(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var value) || value < 0)
            {
                return OperationOrderCandidatesApiQuery.DefaultLimit;
            }

            return value;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
