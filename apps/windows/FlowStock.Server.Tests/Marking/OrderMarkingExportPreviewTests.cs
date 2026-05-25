using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderMarkingExportPreviewTests
{
    [Fact]
    public async Task CustomerOrderPreview_ExcludesReservedWarehouseHu()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 7200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-3600",
            qty: 3600,
            prdDocId: 900);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.GetAsync("/api/orders/10/marking/preview");
        var payload = await response.Content.ReadFromJsonAsync<OrderMarkingPreviewPayload>();

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(payload);
        var line = Assert.Single(payload.Lines);
        Assert.Equal(3600, line.Qty);
        Assert.Equal(0, line.HuCount);
        Assert.Empty(line.HuCodes);
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CustomerOrderPreview_WarehouseBoundPlusProductionPallet_ShowsOnlyProductionQty()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 1200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-WH-600"
        });
        harness.SeedBalance(1, 1, 600, "HU-WH-600");
        harness.SeedDoc(new Doc
        {
            Id = 920,
            DocRef = "PRD-CUSTOMER",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9201,
            PrdDocId = 920,
            DocLineId = 92001,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-PRD-600",
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.GetAsync("/api/orders/10/marking/preview");
        var payload = await response.Content.ReadFromJsonAsync<OrderMarkingPreviewPayload>();

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(payload);
        var line = Assert.Single(payload.Lines);
        Assert.Equal(600, line.Qty);
        Assert.Equal(1, line.HuCount);
        Assert.Equal("HU-PRD-600", Assert.Single(line.HuCodes));
        Assert.Equal(600, payload.TotalQty);
    }

    [Fact]
    public async Task CustomerOrderPreview_AndExport_HaveMatchingQuantities()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 1200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-WH-600"
        });
        harness.SeedBalance(1, 1, 600, "HU-WH-600");
        harness.SeedDoc(new Doc
        {
            Id = 920,
            DocRef = "PRD-CUSTOMER",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9201,
            PrdDocId = 920,
            DocLineId = 92001,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-PRD-600",
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var preview = await host.Client.GetAsync("/api/orders/10/marking/preview");
        var previewPayload = await preview.Content.ReadFromJsonAsync<OrderMarkingPreviewPayload>();
        var export = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(preview.IsSuccessStatusCode);
        Assert.True(export.IsSuccessStatusCode);
        Assert.NotNull(previewPayload);
        Assert.Equal(600, Assert.Single(previewPayload.Lines).Qty);
        Assert.Equal(600, Assert.Single(harness.MarkingOrders).RequestedQuantity);
    }

    [Fact]
    public async Task CustomerOrderPreview_HasNoSideEffects()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 600);
        harness.SeedDoc(new Doc
        {
            Id = 921,
            DocRef = "PRD-PREVIEW",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9211,
            PrdDocId = 921,
            DocLineId = 92101,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-PRD-ONLY",
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.GetAsync("/api/orders/10/marking/preview");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Fact]
    public void CustomerOrderPreview_IncludesReusedCodeQty_WhenExportQtyIsZero()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 1890);
        harness.SeedDoc(new Doc
        {
            Id = 940,
            DocRef = "PRD-REUSED",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9401,
            PrdDocId = 940,
            DocLineId = 94001,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-PRD-1890",
            PlannedQty = 1890,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        var markingOrderId = Guid.NewGuid();
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = markingOrderId,
            OrderId = 10,
            SourceOrderId = 10,
            SourceType = MarkingNeedCreationService.ProductionOrderSourceType,
            ItemId = 1,
            Gtin = "04601234567890",
            RequestedQuantity = 1890,
            Status = MarkingOrderStatus.Printed,
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedMarkingCodes(markingOrderId, count: 1890, gtin: "04601234567890");

        var service = new OrderMarkingExportService(harness.Store, new MarkingExcelService(harness.Store));
        var summary = Assert.Single(service.Export(10, new DateTime(2026, 5, 8, 13, 0, 0, DateTimeKind.Utc)).Lines);
        Assert.Equal(0, summary.ExportQty);
        Assert.Equal(1890, summary.ExistingCodeQty);

        var preview = service.Preview(10);

        Assert.True(preview.IsSuccess);
        var line = Assert.Single(preview.Lines);
        Assert.Equal(1890, line.Qty);
        Assert.Equal(1890, preview.TotalQty);
        Assert.Equal(1, preview.LineCount);
    }

    [Fact]
    public async Task CustomerOrderPreview_AndExportXlsx_HaveMatchingTotalQty_WhenCodesAreReused()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 600);
        harness.SeedDoc(new Doc
        {
            Id = 941,
            DocRef = "PRD-REUSE-XLSX",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9411,
            PrdDocId = 941,
            DocLineId = 94101,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-PRD-600",
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var firstExport = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        Assert.True(firstExport.IsSuccessStatusCode);

        var preview = await host.Client.GetAsync("/api/orders/10/marking/preview");
        var previewPayload = await preview.Content.ReadFromJsonAsync<OrderMarkingPreviewPayload>();
        var secondExport = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(preview.IsSuccessStatusCode);
        Assert.True(secondExport.IsSuccessStatusCode);
        Assert.NotNull(previewPayload);
        Assert.Equal(600, previewPayload.TotalQty);
        Assert.Equal(600, Assert.Single(previewPayload.Lines).Qty);
        Assert.Equal(
            "0",
            secondExport.Headers.GetValues("X-FlowStock-Marking-Created-Qty").Single());
        Assert.Equal(
            "600",
            secondExport.Headers.GetValues("X-FlowStock-Marking-Reused-Qty").Single());
    }

    [Fact]
    public void CustomerOrderPreview_MultipleProductionPallets_ReturnsHuCodesAndCounts()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 2490);
        harness.SeedDoc(new Doc
        {
            Id = 930,
            DocRef = "PRD-MULTI",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        for (var index = 0; index < 5; index++)
        {
            harness.SeedProductionPallet(new ProductionPallet
            {
                Id = 9300 + index,
                PrdDocId = 930,
                DocLineId = 93000 + index,
                OrderId = 10,
                OrderLineId = 100,
                ItemId = 1,
                HuCode = $"HU-PRD-{index + 1:000}",
                PlannedQty = 378,
                ToLocationId = 1,
                Status = ProductionPalletStatus.Planned,
                CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
            });
        }

        var result = new OrderMarkingExportService(harness.Store, new MarkingExcelService(harness.Store)).Preview(10);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Lines);
        Assert.Equal(1890, line.Qty);
        Assert.Equal(5, line.HuCount);
        Assert.Equal(5, line.HuCodes.Count);
    }

    private static void SeedFilledHuReservation(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long itemId,
        long locationId,
        string huCode,
        double qty,
        long prdDocId)
    {
        harness.SeedOrderReceiptPlanLines(orderId, new OrderReceiptPlanLine
        {
            Id = prdDocId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = $"Маркируемый товар {itemId}",
            QtyPlanned = qty,
            ToLocationId = locationId,
            ToHu = huCode
        });
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = $"PRD-{prdDocId}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 11,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = prdDocId * 10,
            PrdDocId = prdDocId,
            DocLineId = 1,
            OrderId = 11,
            ItemId = itemId,
            HuCode = huCode,
            PlannedQty = qty,
            ToLocationId = locationId,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(itemId, locationId, qty, huCode);
    }

    private static CloseDocumentHarness CreateOrderHarness(
        OrderType orderType,
        double qty,
        OrderStatus status = OrderStatus.Draft)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "01", Name = "Склад 01" });
        harness.SeedItem(new Item
        {
            Id = 1,
            Name = "Маркируемый товар 1",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = true
        });
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

    private sealed class OrderMarkingPreviewPayload
    {
        [JsonPropertyName("line_count")]
        public int LineCount { get; init; }

        [JsonPropertyName("total_qty")]
        public double TotalQty { get; init; }

        [JsonPropertyName("lines")]
        public OrderMarkingPreviewLinePayload[] Lines { get; init; } = Array.Empty<OrderMarkingPreviewLinePayload>();
    }

    private sealed class OrderMarkingPreviewLinePayload
    {
        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("hu_count")]
        public int HuCount { get; init; }

        [JsonPropertyName("hu_codes")]
        public string[] HuCodes { get; init; } = Array.Empty<string>();
    }
}
