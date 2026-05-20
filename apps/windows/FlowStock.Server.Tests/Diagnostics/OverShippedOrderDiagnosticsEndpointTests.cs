using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class OverShippedOrderDiagnosticsEndpointTests
{
    [Fact]
    public async Task OverShippedDiagnostics_DetectsDuplicateClosedOutboundLines()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 25, "025", 2500, 3000);
        SeedClosedOutboundLine(harness, 55, "OUT-2026-000055", 25, 5501, 2500, 3000, "HU-000030");
        SeedClosedOutboundLine(harness, 68, "OUT-2026-000068", 25, 6801, 2500, 3000, "HU-000041");

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/diagnostics/over-shipped-orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        var item = Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.Equal(25, item.GetProperty("order_id").GetInt64());
        Assert.Equal("025", item.GetProperty("order_ref").GetString());
        Assert.Equal(1001, item.GetProperty("item_id").GetInt64());
        Assert.Equal(3000, item.GetProperty("qty_ordered").GetDouble());
        Assert.Equal(6000, item.GetProperty("shipped_by_api/read_model").GetDouble());
        Assert.Equal(6000, item.GetProperty("shipped_by_closed_outbound").GetDouble());
        Assert.Equal(6000, item.GetProperty("shipped_by_ledger").GetDouble());
        Assert.Equal(3000, item.GetProperty("over_shipped_qty").GetDouble());
        Assert.Equal(2, item.GetProperty("outbound_docs").GetArrayLength());
        Assert.Equal(2, item.GetProperty("ledger_entries").GetArrayLength());
        Assert.Equal("REAL_OVER_SHIPMENT_REVIEW_REQUIRED", item.GetProperty("recommendation").GetString());
    }

    [Fact]
    public async Task OverShippedDiagnostics_DoesNotFlagNormalFullyShippedOrder()
    {
        var harness = CreateHarness();
        SeedCustomerOrder(harness, 61, "061", 6100, 1800);
        SeedClosedOutboundLine(harness, 151, "OUT-2026-000151", 61, 15101, 6100, 1800, "HU-000061");

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/diagnostics/over-shipped-orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Empty(payload.GetProperty("items").EnumerateArray());
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
        long orderLineId,
        double qtyOrdered)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = 200,
            PartnerName = "Клиент",
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = qtyOrdered,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
    }

    private static void SeedClosedOutboundLine(
        CloseDocumentHarness harness,
        long docId,
        string docRef,
        long orderId,
        long docLineId,
        long orderLineId,
        double qty,
        string huCode)
    {
        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = docRef,
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = orderId,
            OrderRef = orderId.ToString("000"),
            CreatedAt = new DateTime(2026, 5, 7, 11, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = docLineId,
            DocId = docId,
            OrderLineId = orderLineId,
            ItemId = 1001,
            Qty = qty,
            FromHu = huCode
        });
        harness.SeedLedgerEntry(docId, 1001, 1, -qty, huCode);
    }
}
