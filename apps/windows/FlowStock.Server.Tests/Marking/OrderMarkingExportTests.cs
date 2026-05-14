using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderMarkingExportTests
{
    [Fact]
    public async Task InternalOrderExport_CreatesProductionOrderSyntheticCodes()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(3600, markingOrder.RequestedQuantity);
        Assert.Equal(MarkingNeedCreationService.ProductionOrderSourceType, markingOrder.SourceType);
        Assert.Equal(10, markingOrder.SourceOrderId);
        Assert.Equal(10, markingOrder.OrderId);
        Assert.Equal(3600, harness.MarkingCodes.Count(code => code.MarkingOrderId == markingOrder.Id));
    }

    [Fact]
    public async Task ShippedOrderExport_IsRejectedAndCreatesNoMarkingData()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 3600, status: OrderStatus.Shipped);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Нельзя формировать Excel ЧЗ для выполненного заказа.", body, StringComparison.Ordinal);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Fact]
    public async Task AcceptedCustomerOrderExport_IsAllowed()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 3600, status: OrderStatus.Accepted);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Single(harness.MarkingOrders);
    }

    [Fact]
    public async Task InProgressInternalOrderExport_IsAllowed()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 3600, status: OrderStatus.InProgress);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Single(harness.MarkingOrders);
    }

    [Fact]
    public async Task InternalOrderExport_IsIdempotent()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var first = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var second = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            second.Content.Headers.ContentType?.MediaType);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(3600, markingOrder.RequestedQuantity);
        Assert.Equal(3600, harness.MarkingCodes.Count(code => code.MarkingOrderId == markingOrder.Id));
    }

    [Fact]
    public async Task CustomerOrderExport_WhenCodesAlreadyExist_StillRegeneratesExcelWithoutCreatingNewCodes()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 3600, status: OrderStatus.Accepted);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var first = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var codeCountAfterFirst = harness.MarkingCodes.Count;

        var second = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            second.Content.Headers.ContentType?.MediaType);
        Assert.Single(harness.MarkingOrders);
        Assert.Equal(codeCountAfterFirst, harness.MarkingCodes.Count);
    }

    [Fact]
    public async Task InternalProductionReceipt_AfterOrderExport_ClosesAndCompletesOrder()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 5);
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000020",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "INT-010",
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 21,
            DocId = 20,
            OrderLineId = 100,
            ItemId = 1,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-ORD-CHZ-001"
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var export = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var close = harness.CreateService().TryCloseDoc(20, allowNegative: false);

        Assert.True(export.IsSuccessStatusCode);
        Assert.True(close.Success, string.Join("; ", close.Errors));
        Assert.Equal(5, harness.MarkingCodes.Count(code => code.ReceiptLineId == 21));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
    }

    [Fact]
    public async Task CustomerOrderExport_UsesShortageAfterReservedStock()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 7200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 3600,
            ToLocationId = 1
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(3600, markingOrder.RequestedQuantity);
        Assert.Equal(3600, harness.MarkingCodes.Count(code => code.MarkingOrderId == markingOrder.Id));
    }

    [Fact]
    public async Task CustomerOrderExport_FullyCoveredByReservedStock_CreatesNoCodesAndMarksCompleted()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 7200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 7200,
            ToLocationId = 1
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var payload = await response.Content.ReadFromJsonAsync<OrderMarkingExportPayload>();

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(payload);
        Assert.Equal(0, payload.CreatedCodeQty);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
        Assert.True(harness.GetOrder(10).MarkingCompleted);
    }

    [Fact]
    public void OrderBasedExportSummary_IncludesAllMarkableOrderLines()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 10);
        for (var index = 2; index <= 4; index++)
        {
            harness.SeedItem(CreateMarkableItem(index));
            harness.SeedOrderLine(new OrderLine
            {
                Id = 99 + index,
                OrderId = 10,
                ItemId = index,
                QtyOrdered = 10
            });
        }
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар 1",
            QtyPlanned = 10,
            ToLocationId = 1
        });

        var result = new OrderMarkingExportService(harness.Store, new MarkingExcelService(harness.Store))
            .Export(10, new DateTime(2026, 5, 8, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.LineCount);
        Assert.Equal(4, result.Lines.Count);
        Assert.Contains(result.Lines, line => line.OrderLineId == 100 && line.ExportQty == 0);
    }

    private static CloseDocumentHarness CreateOrderHarness(
        OrderType orderType,
        double qty,
        OrderStatus status = OrderStatus.Draft)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "01", Name = "Склад 01" });
        harness.SeedItem(CreateMarkableItem(1));
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = orderType == OrderType.Internal ? "INT-010" : "CO-010",
            Type = orderType,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatus.NotRequired
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 100,
            OrderId = 10,
            ItemId = 1,
            QtyOrdered = qty
        });
        return harness;
    }

    private static Item CreateMarkableItem(long id)
    {
        return new Item
        {
            Id = id,
            Name = $"Маркируемый товар {id}",
            Gtin = $"0460123456789{id % 10}",
            ItemTypeEnableMarking = true
        };
    }

    private sealed class OrderMarkingExportPayload
    {
        [JsonPropertyName("created_code_qty")]
        public double CreatedCodeQty { get; init; }
    }
}
