using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLinesBatchEndpointTests
{
    [Fact]
    public async Task BatchEndpoint_ReturnsSameLinesAsSingleEndpoint()
    {
        var store = CreateFallbackStore();
        await using var host = await OrderLinesHost.StartAsync(store.Object);

        var single10 = await ReadJsonArray(host.Client, "/api/orders/10/lines");
        var single20 = await ReadJsonArray(host.Client, "/api/orders/20/lines");
        var batch = await ReadJsonArray(host.Client, "/api/orders/lines?ids=10,20");

        Assert.Equal(single10.GetRawText(), batch[0].GetProperty("lines").GetRawText());
        Assert.Equal(single20.GetRawText(), batch[1].GetProperty("lines").GetRawText());
    }

    [Fact]
    public async Task BatchEndpoint_UsesOptimizedBatchPath_ForLinesAndProductionHuCodes()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var optimized = store.As<IOptimizedOrderLinesStore>();
        store.As<IOptimizedOrderReadModelStore>();
        store.Setup(data => data.GetOrder(10)).Returns(new Order
        {
            Id = 10,
            OrderRef = "CO-10",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted
        });
        store.Setup(data => data.GetOrder(20)).Returns((Order?)null);
        optimized.Setup(data => data.GetOrderLineViewsByOrderIds(It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 10L, 20L }))))
            .Returns(new Dictionary<long, IReadOnlyList<OrderLineView>>
            {
                [10] =
                [
                    new OrderLineView
                    {
                        Id = 100,
                        OrderId = 10,
                        ItemId = 5,
                        ItemName = "Item A",
                        QtyOrdered = 5,
                        QtyShipped = 1,
                        QtyRemaining = 4,
                        QtyAvailable = 4,
                        CanShipNow = 4,
                        PlannedPalletCount = 2,
                        FilledPalletCount = 1,
                        PlannedPalletQty = 5,
                        FilledPalletQty = 2
                    }
                ],
                [20] = Array.Empty<OrderLineView>()
            });
        optimized.Setup(data => data.GetProductionHuCodesByOrderLineIds(It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 100L }))))
            .Returns(new Dictionary<long, string[]>
            {
                [100] = ["HU-100"]
            });

        await using var host = await OrderLinesHost.StartAsync(store.Object);

        var batch = await ReadJsonArray(host.Client, "/api/orders/lines?ids=10,20");
        var lines10 = batch[0].GetProperty("lines");
        var line = Assert.Single(lines10.EnumerateArray());
        Assert.Equal("HU-100", line.GetProperty("production_hu_codes")[0].GetString());
        Assert.Equal("HU-100", line.GetProperty("production_hu_codes_display").GetString());
        Assert.Equal(2, line.GetProperty("planned_pallet_count").GetInt32());
        Assert.Equal(1, line.GetProperty("filled_pallet_count").GetInt32());
        Assert.Empty(batch[1].GetProperty("lines").EnumerateArray());

        store.Verify(data => data.GetOrder(It.IsAny<long>()), Times.Never);
        store.Verify(data => data.GetOrderLineViews(It.IsAny<long>()), Times.Never);
        store.Verify(data => data.GetOrderReceiptPlanLines(It.IsAny<long>()), Times.Never);
        store.Verify(data => data.GetDocsByOrder(It.IsAny<long>()), Times.Never);
        store.Verify(data => data.GetProductionPalletsByDoc(It.IsAny<long>()), Times.Never);
        optimized.Verify(data => data.GetOrderLineViewsByOrderIds(It.IsAny<IReadOnlyCollection<long>>()), Times.Once);
        optimized.Verify(data => data.GetProductionHuCodesByOrderLineIds(It.IsAny<IReadOnlyCollection<long>>()), Times.Once);
    }

    [Fact]
    public void PostgresOrderLinesReadModel_UsesBatchSqlForLinesAndHuCodes()
    {
        var sql = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("GetOrderLineViewsByOrderIds", sql);
        Assert.Contains("WHERE ol.order_id = ANY(@order_ids)", sql);
        Assert.Contains("available_by_item AS", sql);
        Assert.Contains("shipped_totals AS", sql);
        Assert.Contains("GetProductionHuCodesByOrderLineIds", sql);
        Assert.Contains("WHERE id = ANY(@order_line_ids)", sql);
    }

    [Fact]
    public async Task BatchEndpoint_HandlesEmptyUnknownAndMixedIds()
    {
        var store = CreateFallbackStore();
        await using var host = await OrderLinesHost.StartAsync(store.Object);

        var empty = await ReadJsonArray(host.Client, "/api/orders/lines?ids=");
        Assert.Empty(empty.EnumerateArray());

        var mixed = await ReadJsonArray(host.Client, "/api/orders/lines?ids=999,10");
        Assert.Equal(999, mixed[0].GetProperty("order_id").GetInt64());
        Assert.Empty(mixed[0].GetProperty("lines").EnumerateArray());
        Assert.Equal(10, mixed[1].GetProperty("order_id").GetInt64());
        Assert.NotEmpty(mixed[1].GetProperty("lines").EnumerateArray());

        using var invalid = await host.Client.GetAsync("/api/orders/lines?ids=abc");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static Mock<IDataStore> CreateFallbackStore()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var orders = new Dictionary<long, Order>
        {
            [10] = new()
            {
                Id = 10,
                OrderRef = "CO-10",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress
            },
            [20] = new()
            {
                Id = 20,
                OrderRef = "IO-20",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress
            }
        };
        var lines = new Dictionary<long, IReadOnlyList<OrderLineView>>
        {
            [10] =
            [
                new OrderLineView
                {
                    Id = 100,
                    OrderId = 10,
                    ItemId = 5,
                    ItemName = "Item A",
                    QtyOrdered = 5,
                    PlannedPalletCount = 1,
                    PlannedPalletQty = 5
                }
            ],
            [20] =
            [
                new OrderLineView
                {
                    Id = 200,
                    OrderId = 20,
                    ItemId = 6,
                    ItemName = "Item B",
                    QtyOrdered = 7,
                    PlannedPalletCount = 2,
                    FilledPalletCount = 1,
                    PlannedPalletQty = 7,
                    FilledPalletQty = 3
                }
            ]
        };

        store.Setup(data => data.GetOrder(It.IsAny<long>()))
            .Returns<long>(orderId => orders.TryGetValue(orderId, out var order) ? order : null);
        store.Setup(data => data.GetOrderLineViews(It.IsAny<long>()))
            .Returns<long>(orderId => lines.TryGetValue(orderId, out var orderLines) ? CloneLines(orderLines) : Array.Empty<OrderLineView>());
        store.Setup(data => data.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(orderId => lines.TryGetValue(orderId, out var orderLines)
                ? orderLines.Select(line => new OrderLine
                {
                    Id = line.Id,
                    OrderId = line.OrderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered,
                    ProductionPurpose = line.ProductionPurpose,
                    ProductionPalletGroup = line.ProductionPalletGroup
                }).ToList()
                : Array.Empty<OrderLine>());
        store.Setup(data => data.GetLedgerTotalsByItem())
            .Returns(new Dictionary<long, double>
            {
                [5] = 10,
                [6] = 3
            });
        store.Setup(data => data.GetShippedTotalsByOrderLine(It.IsAny<long>()))
            .Returns<long>(orderId => orderId == 10 ? new Dictionary<long, double> { [100] = 2 } : new Dictionary<long, double>());
        store.Setup(data => data.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns<long>(orderId => orderId switch
            {
                10 =>
                [
                    new OrderReceiptLine
                    {
                        OrderLineId = 100,
                        OrderId = 10,
                        ItemId = 5,
                        QtyReceived = 1
                    }
                ],
                20 =>
                [
                    new OrderReceiptLine
                    {
                        OrderLineId = 200,
                        OrderId = 20,
                        ItemId = 6,
                        QtyReceived = 3
                    }
                ],
                _ => Array.Empty<OrderReceiptLine>()
            });
        store.Setup(data => data.FindItemById(It.IsAny<long>())).Returns((Item?)null);
        store.Setup(data => data.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(orderId => orderId == 10
                ?
                [
                    new OrderReceiptPlanLine
                    {
                        OrderId = 10,
                        OrderLineId = 100,
                        ItemId = 5,
                        QtyPlanned = 2,
                        ToHu = "HU-PLAN"
                    }
                ]
                : Array.Empty<OrderReceiptPlanLine>());
        store.Setup(data => data.GetDocsByOrder(It.IsAny<long>())).Returns(Array.Empty<Doc>());

        return store;
    }

    private static IReadOnlyList<OrderLineView> CloneLines(IReadOnlyList<OrderLineView> lines)
    {
        return lines
            .Select(line => new OrderLineView
            {
                Id = line.Id,
                OrderId = line.OrderId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                Barcode = line.Barcode,
                Gtin = line.Gtin,
                QtyOrdered = line.QtyOrdered,
                ProductionPurpose = line.ProductionPurpose,
                ProductionPalletGroup = line.ProductionPalletGroup,
                PlannedPalletCount = line.PlannedPalletCount,
                FilledPalletCount = line.FilledPalletCount,
                PlannedPalletQty = line.PlannedPalletQty,
                FilledPalletQty = line.FilledPalletQty
            })
            .ToList();
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

    private sealed class OrderLinesHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private OrderLinesHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<OrderLinesHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderLinesEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);

            var app = builder.Build();
            OrderLinesEndpoint.Map(app);
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

            return new OrderLinesHost(app, new HttpClient
            {
                BaseAddress = new Uri(address, UriKind.Absolute)
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
