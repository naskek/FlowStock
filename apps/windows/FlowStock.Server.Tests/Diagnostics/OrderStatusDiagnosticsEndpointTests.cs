using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class OrderStatusDiagnosticsEndpointTests
{
    [Fact]
    public async Task RefreshFullyShipped_DryRun_FindsStaleCustomerStatuses()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 53, "053", OrderStatus.Accepted, 3780, 3780);
        SeedCustomerOrder(harness, 57, "057", OrderStatus.Accepted, 1680, 1680);
        SeedCustomerOrder(harness, 58, "058", OrderStatus.InProgress, 1498, 1498);
        SeedCustomerOrder(harness, 61, "061", OrderStatus.InProgress, 1800, 1800);
        SeedCustomerOrder(harness, 70, "070", OrderStatus.InProgress, 1800, 600);
        SeedCustomerOrder(harness, 80, "080", OrderStatus.Shipped, 1800, 1800);
        SeedCustomerOrder(harness, 81, "081", OrderStatus.Cancelled, 1800, 1800);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.PostAsJsonAsync(
            "/api/diagnostics/order-status/refresh-fully-shipped",
            new { dry_run = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("dry_run").GetBoolean());
        Assert.Equal(4, payload.GetProperty("refreshed_count").GetInt32());

        var refs = payload.GetProperty("orders")
            .EnumerateArray()
            .Select(row => row.GetProperty("order_ref").GetString())
            .ToArray();
        Assert.Contains("053", refs);
        Assert.Contains("057", refs);
        Assert.Contains("058", refs);
        Assert.Contains("061", refs);
        Assert.DoesNotContain("070", refs);
        Assert.DoesNotContain("080", refs);
        Assert.DoesNotContain("081", refs);
    }

    [Fact]
    public async Task RefreshFullyShipped_Apply_UpdatesStaleCustomerStatusToShipped()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 61, "061", OrderStatus.InProgress, 1800, 1800);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.PostAsJsonAsync(
            "/api/diagnostics/order-status/refresh-fully-shipped",
            new { apply = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("dry_run").GetBoolean());
        Assert.Equal(1, payload.GetProperty("changed_count").GetInt32());
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(61).Status);

        var row = Assert.Single(payload.GetProperty("orders").EnumerateArray());
        Assert.Equal("IN_PROGRESS", row.GetProperty("old_status").GetString());
        Assert.Equal("SHIPPED", row.GetProperty("new_status").GetString());
        Assert.True(row.GetProperty("updated").GetBoolean());
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST",
            Name = "Клиент",
            CreatedAt = new DateTime(2026, 5, 7, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            BaseUom = "шт",
            ItemTypeName = "Готовая продукция"
        });
        return harness;
    }

    private static void SeedCustomerOrder(
        CloseDocumentHarness harness,
        long orderId,
        string orderRef,
        OrderStatus status,
        double qtyOrdered,
        double qtyShipped)
    {
        var lineId = orderId * 100;
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = 200,
            PartnerName = "Клиент",
            Status = status,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = qtyOrdered,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedShippedTotalsByOrderLine(orderId, new Dictionary<long, double>
        {
            [lineId] = qtyShipped
        });
    }
}
