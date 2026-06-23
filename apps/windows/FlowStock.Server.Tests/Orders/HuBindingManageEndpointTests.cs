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

public sealed class HuBindingManageEndpointTests
{
    private const long ItemA = 6;
    private const long OrderA = 50;
    private const long OrderB = 51;
    private const long LineA = 5000;
    private const long LineB = 5100;

    [Fact]
    public async Task ItemsEndpoint_ReturnsItemsContract()
    {
        var harness = CreateHarness();
        harness.SeedBalance(ItemA, 1, 100, "HU-1");
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/orders/hu-bindings/manage/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var item = doc.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(ItemA, item.GetProperty("item_id").GetInt64());
        Assert.Equal(1, item.GetProperty("hu_count").GetInt32());
    }

    [Fact]
    public async Task HusEndpoint_ReturnsHuRowsAndPagination()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, 1, 100, "HU-FREE");
        harness.SeedBalance(ItemA, 1, 600, "HU-BOUND");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-BOUND", 600));
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus?limit=10&offset=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ItemA, doc.RootElement.GetProperty("item_id").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        var rows = doc.RootElement.GetProperty("hu_rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var bound = rows.Single(row => row.GetProperty("hu_code").GetString() == "HU-BOUND");
        Assert.Equal("BOUND", bound.GetProperty("state").GetString());
        Assert.Equal(OrderA, bound.GetProperty("current_assignment").GetProperty("order_id").GetInt64());
    }

    [Fact]
    public async Task TargetsEndpoint_ReturnsTargetLines()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, 1, 200, "HU-10");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-10", 200));
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/targets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var line = doc.RootElement.GetProperty("target_lines").EnumerateArray().Single();
        Assert.Equal(OrderA, line.GetProperty("order_id").GetInt64());
        Assert.Equal("HU-10", line.GetProperty("current_bound_hu_codes").EnumerateArray().Single().GetString());
        Assert.Equal(400, line.GetProperty("max_additional_bind_qty").GetDouble(), 3);
    }

    [Fact]
    public async Task HusEndpoint_InvalidItemId_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/orders/hu-bindings/manage/items/0/hus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_ITEM_ID", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HusEndpoint_InvalidState_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus?state=BOGUS");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_STATE", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HusEndpoint_InvalidLimit_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus?limit=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_LIMIT", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HusEndpoint_ZeroLimit_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_LIMIT", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HusEndpoint_LimitAboveMaximum_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus?limit=501");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_LIMIT", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HusEndpoint_InvalidOffset_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus?offset=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_OFFSET", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TargetsEndpoint_InvalidItemId_Returns400()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/orders/hu-bindings/manage/items/0/targets");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_ITEM_ID", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HusEndpoint_EmptyResults_ReturnsEmpty()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync($"/api/orders/hu-bindings/manage/items/{ItemA}/hus");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("hu_rows").EnumerateArray());
    }

    [Fact]
    public async Task ApplyEndpoint_BindSuccess()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, 1, 600, "HU-NEW");
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/hu-bindings/manage/apply-final",
            new
            {
                mode = "replace_final_selection",
                expected_hu_states = Array.Empty<object>(),
                lines = new[]
                {
                    new
                    {
                        order_id = OrderA,
                        order_line_id = LineA,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = new[] { "HU-NEW" }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var order = doc.RootElement.GetProperty("orders").EnumerateArray().Single();
        var line = order.GetProperty("applied_lines").EnumerateArray().Single();
        Assert.Equal("HU-NEW", line.GetProperty("bound_hu_codes").EnumerateArray().Single().GetString());
        Assert.Equal("HU-NEW", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
    }

    [Fact]
    public async Task ApplyEndpoint_Stale_ReturnsError()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, 1, 600, "HU-OLD");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, "HU-OLD", 600));
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/hu-bindings/manage/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_id = OrderA,
                        order_line_id = LineA,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("HU_BINDING_STALE", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApplyEndpoint_DuplicateHu_ReturnsValidationError()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        SeedLine(harness, LineA + 1, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, 1, 600, "HU-1");
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/hu-bindings/manage/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new { order_id = OrderA, order_line_id = LineA, expected_bound_hu_codes = Array.Empty<string>(), final_hu_codes = new[] { "HU-1" } },
                    new { order_id = OrderA, order_line_id = LineA + 1, expected_bound_hu_codes = Array.Empty<string>(), final_hu_codes = new[] { "HU-1" } }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DUPLICATE_HU_IN_REQUEST", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApplyEndpoint_OwnershipConflict_Returns409()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        SeedOrder(harness, OrderB, "SO-B", OrderStatus.InProgress);
        SeedLine(harness, LineB, OrderB, ItemA, 600);
        harness.SeedBalance(ItemA, 1, 600, "HU-OTHER");
        harness.SeedOrderReceiptPlanLines(OrderB, Plan(OrderB, LineB, "HU-OTHER", 600));
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/hu-bindings/manage/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_id = OrderA,
                        order_line_id = LineA,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = new[] { "HU-OTHER" }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApplyEndpoint_UnknownOrder_Returns404()
    {
        var harness = CreateHarness();
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/hu-bindings/manage/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_id = 999L,
                        order_line_id = LineA,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("ORDER_NOT_FOUND", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApplyEndpoint_ClosedOrder_Returns403()
    {
        var harness = CreateHarness();
        SeedOrder(harness, OrderA, "SO-A", OrderStatus.Shipped);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        await using var host = await ManageHost.StartAsync(harness.Store);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/hu-bindings/manage/apply-final",
            new
            {
                mode = "replace_final_selection",
                lines = new[]
                {
                    new
                    {
                        order_id = OrderA,
                        order_line_id = LineA,
                        expected_bound_hu_codes = Array.Empty<string>(),
                        final_hu_codes = Array.Empty<string>()
                    }
                }
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("ORDER_CLOSED", doc.RootElement.GetProperty("error").GetString());
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItemType(new ItemType { Id = 1, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item { Id = ItemA, Name = "Горчица", BaseUom = "шт", ItemTypeId = 1, MaxQtyPerHu = 600 });
        harness.SeedPartner(new Partner { Id = 200, Code = "CUST", Name = "Клиент" });
        return harness;
    }

    private static void SeedOrder(CloseDocumentHarness harness, long orderId, string orderRef, OrderStatus status)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = status,
            PartnerId = 200,
            PartnerName = "Клиент",
            CreatedAt = DateTime.UtcNow
        });
    }

    private static void SeedLine(CloseDocumentHarness harness, long lineId, long orderId, long itemId, double qty)
    {
        harness.SeedOrderLine(new OrderLine { Id = lineId, OrderId = orderId, ItemId = itemId, QtyOrdered = qty });
    }

    private static OrderReceiptPlanLine Plan(long orderId, long lineId, string huCode, double qty) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = ItemA,
            QtyPlanned = qty,
            ToHu = huCode,
            ToLocationId = 1
        };

    private sealed class ManageHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ManageHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<ManageHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(HuBindingManageReadEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            HuBindingManageReadEndpoint.Map(app);
            OrderHuBindingManageApplyEndpoint.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new ManageHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
