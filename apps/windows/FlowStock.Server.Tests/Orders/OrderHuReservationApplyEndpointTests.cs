using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderHuReservationApplyEndpointTests
{
    [Fact]
    public async Task ApplyEndpoint_UsesOrderScopedPostPath()
    {
        var context = new HuReservationApplyTestContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        await using var host = await ApplyHost.StartAsync(context.Store);

        using var getResponse = await host.Client.GetAsync("/api/orders/78/hu-reservations/apply");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);

        using var postResponse = await host.Client.PostAsJsonAsync(
            "/api/orders/78/hu-reservations/apply",
            new { lines = new[] { new { order_line_id = 203L, selected_hu_codes = Array.Empty<string>() } } });
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
    }

    [Fact]
    public async Task ApplyEndpoint_ReturnsAppliedLines()
    {
        var context = new HuReservationApplyTestContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-0000493", 6, 600, true));
        await using var host = await ApplyHost.StartAsync(context.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/78/hu-reservations/apply",
            new
            {
                lines = new[]
                {
                    new { order_line_id = 203L, selected_hu_codes = new[] { "HU-0000493" } }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(78, document.RootElement.GetProperty("order_id").GetInt64());
        var applied = document.RootElement.GetProperty("applied_lines").EnumerateArray().Single();
        Assert.Equal(600, applied.GetProperty("reserved_qty").GetDouble(), 3);
    }

    [Fact]
    public async Task ApplyEndpoint_ReturnsErrorCode()
    {
        var context = new HuReservationApplyTestContext();
        context.SeedInternalOrder(72, 501, itemId: 6, qtyOrdered: 600);
        await using var host = await ApplyHost.StartAsync(context.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/72/hu-reservations/apply",
            new { lines = new[] { new { order_line_id = 501L, selected_hu_codes = new[] { "HU-1" } } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("ORDER_NOT_CUSTOMER", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void CustomerBoundHu_AllowsOutboundCloseWhileInternalPrdStillOpen()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Filled));
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 1001,
            QtyPlanned = 5,
            ToHu = "HU-000001"
        });
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = new DateTime(2026, 5, 13, 10, 0, 0),
            DocId = 10,
            ItemId = 1001,
            LocationId = 1,
            QtyDelta = 5,
            HuCode = "HU-000001"
        });
        harness.SeedDoc(new Doc
        {
            Id = 30,
            DocRef = "OUT-2026-000001",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            PartnerId = 1,
            OrderId = 20,
            CreatedAt = new DateTime(2026, 5, 13, 12, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 30,
            OrderLineId = 201,
            ItemId = 1001,
            Qty = 5,
            FromLocationId = 1,
            FromHu = "HU-000001"
        });

        var result = harness.CreateService().TryCloseDoc(30, allowNegative: false);

        Assert.True(result.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(30).Status);
    }

    [Fact]
    public void CustomerOutboundWithoutPlanBinding_StillBlockedUntilPrdClose()
    {
        var harness = CreateHarness();
        harness.SeedProductionPallet(BuildPallet(ProductionPalletStatus.Filled));
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = new DateTime(2026, 5, 13, 10, 0, 0),
            DocId = 10,
            ItemId = 1001,
            LocationId = 1,
            QtyDelta = 5,
            HuCode = "HU-000001"
        });
        harness.SeedDoc(new Doc
        {
            Id = 30,
            DocRef = "OUT-2026-000001",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            PartnerId = 1,
            OrderId = 20,
            CreatedAt = new DateTime(2026, 5, 13, 12, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 30,
            OrderLineId = 201,
            ItemId = 1001,
            Qty = 5,
            FromLocationId = 1,
            FromHu = "HU-000001"
        });

        var result = harness.CreateService().TryCloseDoc(30, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("ожидает закрытия PRD", StringComparison.OrdinalIgnoreCase));
    }

    private static HuReservationCandidateSourceRow Source(
        string source,
        string huCode,
        long itemId,
        double qty,
        bool shipReady) =>
        new()
        {
            Source = source,
            HuCode = huCode,
            ItemId = itemId,
            Qty = qty,
            ShipReady = shipReady
        };

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 1001, Name = "Горчица", ItemTypeId = 1 });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedLocation(new Location { Id = 1, Code = "FG-01", Name = "FG-01" });
        harness.SeedPartner(new Partner { Id = 1, Name = "Клиент" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "INT-010",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 5
        });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-020",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 1
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5
        });
        harness.SeedDoc(new Doc
        {
            Id = 10,
            DocRef = "PRD-2026-000010",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 101,
            DocId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-000001"
        });
        return harness;
    }

    private static ProductionPallet BuildPallet(string status) => new()
    {
        Id = 1,
        PrdDocId = 10,
        DocLineId = 101,
        OrderId = 10,
        OrderLineId = 101,
        ItemId = 1001,
        HuCode = "HU-000001",
        Status = status,
        PlannedQty = 5,
        FilledAt = string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
            ? new DateTime(2026, 5, 13, 10, 0, 0)
            : null
    };

    private sealed class ApplyHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ApplyHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<ApplyHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderHuReservationApplyEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            OrderHuReservationCandidatesEndpoint.Map(app);
            OrderHuReservationApplyEndpoint.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new ApplyHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

internal sealed class HuReservationApplyTestContext
{
    private readonly Mock<IDataStore> _store = new();
    private readonly Dictionary<long, Order> _orders = new();
    private readonly Dictionary<long, List<OrderLine>> _orderLines = new();
    private readonly Dictionary<long, List<OrderShipmentLine>> _shipmentRemaining = new();
    private readonly Dictionary<long, List<OrderReceiptPlanLine>> _planLines = new();
    private readonly List<HuReservationCandidateSourceRow> _sources = new();
    private long _nextPlanLineId = 1;

    public HuReservationApplyTestContext()
    {
        _store.As<IOptimizedHuReservationCandidatesStore>();
        _store.Setup(store => store.GetOrder(It.IsAny<long>()))
            .Returns<long>(id => _orders.TryGetValue(id, out var order) ? order : null);
        _store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(id => _orderLines.TryGetValue(id, out var lines) ? lines.ToArray() : Array.Empty<OrderLine>());
        _store.Setup(store => store.GetOrderShipmentRemaining(It.IsAny<long>()))
            .Returns<long>(id => _shipmentRemaining.TryGetValue(id, out var lines) ? lines.ToArray() : Array.Empty<OrderShipmentLine>());
        _store.Setup(store => store.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(id => _planLines.TryGetValue(id, out var lines) ? lines.ToArray() : Array.Empty<OrderReceiptPlanLine>());
        _store.Setup(store => store.GetReservedOrderReceiptHuCodes(It.IsAny<long?>()))
            .Returns<long?>(excludeOrderId => _planLines
                .Where(pair => !excludeOrderId.HasValue || pair.Key != excludeOrderId.Value)
                .SelectMany(pair => pair.Value)
                .Select(line => line.ToHu)
                .Where(huCode => !string.IsNullOrWhiteSpace(huCode))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        _store.Setup(store => store.ReplaceOrderReceiptPlanLinesForOrderLines(
                It.IsAny<long>(),
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyCollection<long>, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lineIds, replacement) =>
            {
                if (!_planLines.TryGetValue(orderId, out var current))
                {
                    current = [];
                    _planLines[orderId] = current;
                }

                var affected = lineIds.ToHashSet();
                current.RemoveAll(line => affected.Contains(line.OrderLineId));
                foreach (var line in replacement)
                {
                    current.Add(new OrderReceiptPlanLine
                    {
                        Id = _nextPlanLineId++,
                        OrderId = orderId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        QtyPlanned = line.QtyPlanned,
                        ToHu = line.ToHu,
                        SortOrder = line.SortOrder
                    });
                }
            });
        _store.As<IOptimizedHuReservationCandidatesStore>()
            .Setup(store => store.GetHuReservationCandidateSources(
                It.IsAny<long?>(),
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<IReadOnlyCollection<string>>()))
            .Returns<long?, IReadOnlyCollection<long>, IReadOnlyCollection<string>>((_, itemIds, excludeHuCodes) =>
            {
                var exclude = new HashSet<string>(excludeHuCodes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                return _sources.Where(row => itemIds.Contains(row.ItemId) && !exclude.Contains(row.HuCode)).ToArray();
            });
    }

    public IDataStore Store => _store.Object;

    public void SeedCustomerOrder(long orderId, long lineId, long itemId, double qtyOrdered, OrderStatus status = OrderStatus.InProgress)
    {
        _orders[orderId] = new Order
        {
            Id = orderId,
            OrderRef = $"SO-{orderId:000}",
            Type = OrderType.Customer,
            Status = status,
            PartnerId = 1
        };
        _orderLines[orderId] = [new OrderLine { Id = lineId, OrderId = orderId, ItemId = itemId, QtyOrdered = qtyOrdered }];
        _shipmentRemaining[orderId] =
        [
            new OrderShipmentLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = qtyOrdered,
                QtyShipped = 0,
                QtyRemaining = qtyOrdered
            }
        ];
    }

    public void SeedInternalOrder(long orderId, long lineId, long itemId, double qtyOrdered)
    {
        _orders[orderId] = new Order
        {
            Id = orderId,
            OrderRef = $"INT-{orderId:000}",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress
        };
        _orderLines[orderId] = [new OrderLine { Id = lineId, OrderId = orderId, ItemId = itemId, QtyOrdered = qtyOrdered }];
    }

    public void SeedCandidate(HuReservationCandidateSourceRow source) => _sources.Add(source);
}
