using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLinesBatchEndpointTests
{
    [Fact]
    public async Task BatchEndpoint_PreservesExistingFieldsWithoutSingleEndpointDetails()
    {
        var store = CreateFallbackStore();
        await using var host = await OrderLinesHost.StartAsync(store.Object);

        var single10 = await ReadJsonArray(host.Client, "/api/orders/10/lines");
        var batch = await ReadJsonArray(host.Client, "/api/orders/lines?ids=10,20");

        var singleLine = Assert.Single(single10.EnumerateArray());
        var batchLine = Assert.Single(batch[0].GetProperty("lines").EnumerateArray());
        foreach (var propertyName in new[]
                 {
                     "id", "order_id", "item_id", "item_name", "qty_ordered", "production_hu_codes",
                     "qty_shipped", "qty_produced", "qty_left", "qty_available", "can_ship_now", "shortage",
                     "planned_pallet_count", "filled_pallet_count", "pallet_planned_qty", "pallet_filled_qty"
                 })
        {
            Assert.Equal(singleLine.GetProperty(propertyName).GetRawText(), batchLine.GetProperty(propertyName).GetRawText());
        }

        Assert.True(singleLine.TryGetProperty("warehouse_hu_rows", out _));
        Assert.True(singleLine.TryGetProperty("production_hu_rows", out _));
        Assert.True(singleLine.TryGetProperty("shipped_hu_rows", out _));
        Assert.True(singleLine.TryGetProperty("coverage", out _));
        Assert.False(batchLine.TryGetProperty("warehouse_hu_rows", out _));
        Assert.False(batchLine.TryGetProperty("production_hu_rows", out _));
        Assert.False(batchLine.TryGetProperty("shipped_hu_rows", out _));
        Assert.False(batchLine.TryGetProperty("coverage", out _));
    }

    [Fact]
    public async Task SingleEndpoint_LogsDetailedPerformancePhases_WhileBatchDoesNot()
    {
        var store = CreateFallbackStore();
        var loggerProvider = new CapturingLoggerProvider("FlowStock.Server.OrderLinesPerformance");
        await using var host = await OrderLinesHost.StartAsync(store.Object, loggerProvider);

        var single = await ReadJsonArray(host.Client, "/api/orders/10/lines");
        Assert.Single(single.EnumerateArray());

        var logs = loggerProvider.Messages.Where(message => message.StartsWith("PERF ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, logs.Length);
        Assert.Contains(logs, message => message.StartsWith("PERF order-lines-single ", StringComparison.Ordinal)
                                         && message.Contains("order_id=10", StringComparison.Ordinal)
                                         && message.Contains("result=OK", StringComparison.Ordinal)
                                         && message.Contains("order_lookup_ms=", StringComparison.Ordinal)
                                         && message.Contains("hu_details_ms=", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.StartsWith("PERF order-line-hu-details ", StringComparison.Ordinal)
                                         && message.Contains("order_id=10", StringComparison.Ordinal)
                                         && message.Contains("build_warehouse_rows_ms=", StringComparison.Ordinal)
                                         && message.Contains("customer_coverage_ms=", StringComparison.Ordinal)
                                         && message.Contains("hu_fate_ms=0", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.StartsWith("PERF hu-fate ", StringComparison.Ordinal)
                                         && message.Contains("order_id=10", StringComparison.Ordinal)
                                         && message.Contains("skipped=True", StringComparison.Ordinal)
                                         && message.Contains("orders_count=", StringComparison.Ordinal)
                                         && message.Contains("shipments_count=", StringComparison.Ordinal)
                                         && message.Contains("final_rows_count=0", StringComparison.Ordinal)
                                         && message.Contains("total_ms=0", StringComparison.Ordinal));
        store.Verify(data => data.GetOrders(), Times.Never);
        store.Verify(data => data.GetDocs(), Times.Never);

        loggerProvider.Clear();
        var batch = await ReadJsonArray(host.Client, "/api/orders/lines?ids=10,20");
        Assert.Equal(2, batch.GetArrayLength());
        Assert.DoesNotContain(loggerProvider.Messages, message => message.StartsWith("PERF ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleEndpoint_NotFound_LogsOnlyEndpointPerformance()
    {
        var store = CreateFallbackStore();
        var loggerProvider = new CapturingLoggerProvider("FlowStock.Server.OrderLinesPerformance");
        await using var host = await OrderLinesHost.StartAsync(store.Object, loggerProvider);

        using var response = await host.Client.GetAsync("/api/orders/999/lines");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var log = Assert.Single(loggerProvider.Messages.Where(message => message.StartsWith("PERF ", StringComparison.Ordinal)));
        Assert.StartsWith("PERF order-lines-single ", log, StringComparison.Ordinal);
        Assert.Contains("order_id=999", log, StringComparison.Ordinal);
        Assert.Contains("result=NOT_FOUND", log, StringComparison.Ordinal);
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
        store.Setup(data => data.GetOrders()).Returns(orders.Values.ToArray());
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
        store.Setup(data => data.GetHuStockRows())
            .Returns([
                new HuStockRow
                {
                    ItemId = 5,
                    LocationId = 1,
                    HuCode = "HU-PLAN",
                    Qty = 2
                }
            ]);
        store.Setup(data => data.GetLocations())
            .Returns([
                new Location
                {
                    Id = 1,
                    Code = "MAIN",
                    Name = "Основной склад"
                }
            ]);
        store.Setup(data => data.GetDocsByOrder(It.IsAny<long>())).Returns(Array.Empty<Doc>());
        store.Setup(data => data.GetDocs()).Returns(Array.Empty<Doc>());

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

        public static async Task<OrderLinesHost> StartAsync(
            IDataStore store,
            ILoggerProvider? loggerProvider = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderLinesEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            if (loggerProvider != null)
            {
                builder.Logging.AddProvider(loggerProvider);
            }

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

    private sealed class CapturingLoggerProvider(string category) : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName == category ? _messages : null);

        public void Clear()
        {
            while (_messages.TryDequeue(out _))
            {
            }
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string>? messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => messages != null && logLevel == LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    messages!.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
