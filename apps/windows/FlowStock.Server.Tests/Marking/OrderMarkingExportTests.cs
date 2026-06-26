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
        Assert.Null(markingOrder.OrderLineId);
        Assert.Equal(3600, harness.MarkingCodes.Count(code => code.MarkingOrderId == markingOrder.Id));
        Assert.True(harness.GetOrder(10).MarkingCompleted);
        Assert.Equal(MarkingStatus.Printed, harness.GetOrder(10).EffectiveMarkingStatus);
    }

    [Fact]
    public async Task LegacyOrderExport_WithSameItemOnMultipleLines_DoesNotScopeAggregatedTaskToFirstLine()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 5);
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1,
            QtyOrdered = 7
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(12, markingOrder.RequestedQuantity);
        Assert.Null(markingOrder.OrderLineId);
        Assert.Equal(12, harness.MarkingCodes.Count(code => code.MarkingOrderId == markingOrder.Id));
    }

    [Fact]
    public async Task LegacyOrderExport_RepeatedExport_KeepsTaskUnscopedForActiveLineUniqueIndexCompatibility()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 5);
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1,
            QtyOrdered = 7
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var first = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var second = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Null(markingOrder.OrderLineId);
        Assert.Equal(12, markingOrder.RequestedQuantity);
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
    public void NewInternalMarkingOrder_WithoutExcelOrCodes_IsNotCompleted()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 3600, status: OrderStatus.InProgress);

        var order = harness.GetOrder(10);

        Assert.True(order.MarkingApplies);
        Assert.True(order.MarkingRequired);
        Assert.False(order.MarkingCompleted);
        Assert.Equal(MarkingStatus.Required, order.EffectiveMarkingStatus);
        Assert.Equal("Маркировка не проведена", order.MarkingLabel);
    }

    [Fact]
    public void NewInternalMarkingOrder_WithZeroNeedAndNoCodes_IsNotCompleted()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 0, status: OrderStatus.InProgress);

        var order = harness.GetOrder(10);

        Assert.True(order.MarkingApplies);
        Assert.True(order.MarkingRequired);
        Assert.False(order.MarkingCompleted);
        Assert.Equal(MarkingStatus.Required, order.EffectiveMarkingStatus);
        Assert.Equal("Маркировка не проведена", order.MarkingLabel);
    }

    [Fact]
    public void NewInternalMarkingOrder_WithUnrelatedFreeCodes_IsNotCompleted()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 3600, status: OrderStatus.InProgress);
        var markingOrderId = Guid.NewGuid();
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = markingOrderId,
            OrderId = null,
            SourceOrderId = null,
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            ItemId = 1,
            Gtin = "04601234567890",
            RequestedQuantity = 3600,
            Status = MarkingOrderStatus.Printed,
            CreatedAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedMarkingCodes(markingOrderId, count: 3600, gtin: "04601234567890");

        var order = harness.GetOrder(10);

        Assert.True(order.MarkingApplies);
        Assert.True(order.MarkingRequired);
        Assert.False(order.MarkingCompleted);
        Assert.Equal(MarkingStatus.Required, order.EffectiveMarkingStatus);
    }

    [Fact]
    public void FilledPallet_DoesNotMakeInternalOrderMarkingCompleted()
    {
        var harness = CreateOrderHarness(OrderType.Internal, qty: 3600, status: OrderStatus.InProgress);
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
            Qty = 3600,
            ToLocationId = 1,
            ToHu = "HU-FILLED-CHZ-001"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 2001,
            PrdDocId = 20,
            DocLineId = 21,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-FILLED-CHZ-001",
            PlannedQty = 3600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 8, 13, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });

        var order = harness.GetOrder(10);

        Assert.True(order.MarkingApplies);
        Assert.True(order.MarkingRequired);
        Assert.False(order.MarkingCompleted);
        Assert.Equal(MarkingStatus.Required, order.EffectiveMarkingStatus);
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
        Assert.True(harness.GetOrder(10).MarkingCompleted);
        Assert.Equal("Маркировка проведена", harness.GetOrder(10).MarkingLabel);
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
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-7200",
            qty: 7200,
            prdDocId: 901);
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
        Assert.Contains(result.Lines, line => line.OrderLineId == 100 && line.ExportQty == 10);
    }

    [Fact]
    public async Task CustomerOrderExport_M1_ReservedFilledHu_ReducesExportQty()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-100",
            qty: 100,
            prdDocId: 902);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(100, markingOrder.RequestedQuantity);
        Assert.Equal(100, harness.MarkingCodes.Count(code => code.MarkingOrderId == markingOrder.Id));
    }

    [Fact]
    public async Task CustomerOrderExport_M2_SecondExport_IsIdempotentWithoutNewCodes()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-100",
            qty: 100,
            prdDocId: 903);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var first = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var codeCountAfterFirst = harness.MarkingCodes.Count;

        var second = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
        Assert.Equal(codeCountAfterFirst, harness.MarkingCodes.Count);
    }

    [Fact]
    public async Task CustomerOrderExport_M3_ReservedOnOtherLine_DoesNotReduceTargetLineShortage()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        harness.SeedItem(CreateMarkableItem(2));
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 2,
            QtyOrdered = 50
        });
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 101,
            itemId: 2,
            locationId: 1,
            huCode: "HU-OTHER-ITEM",
            qty: 50,
            prdDocId: 904);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(200, markingOrder.RequestedQuantity);
    }

    [Fact]
    public async Task CustomerOrderExport_M4_WarehouseBoundHu_ReducesProductionMarkingShortage()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 100,
            ToLocationId = 1,
            ToHu = "HU-PLANNED-ONLY"
        });
        harness.SeedDoc(new Doc
        {
            Id = 905,
            DocRef = "PRD-PLANNED",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 11,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9051,
            PrdDocId = 905,
            DocLineId = 1,
            OrderId = 11,
            ItemId = 1,
            HuCode = "HU-PLANNED-ONLY",
            PlannedQty = 100,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(1, 1, 100, "HU-PLANNED-ONLY");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(100, markingOrder.RequestedQuantity);
    }

    [Fact]
    public async Task CustomerOrderExport_WarehouseBoundPlusProductionPallet_ExportsOnlyProductionQty()
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

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Equal(600, markingOrder.RequestedQuantity);
        Assert.True(harness.GetOrder(10).MarkingCompleted);
        Assert.Equal(MarkingStatus.Printed, harness.GetOrder(10).EffectiveMarkingStatus);
        Assert.Equal("Маркировка проведена", harness.GetOrder(10).MarkingLabel);
    }

    [Fact]
    public void PostgresOrderReadModel_UsesWarehouseReservedHuForCustomerMarkingStatus()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

        Assert.Contains("reserved_stock_hu_by_line", source);
        Assert.Contains("SUM(LEAST(p.qty_planned, ls.qty)) AS reserved_stock_hu_qty", source);
        Assert.Contains("COALESCE(rsh.reserved_stock_hu_qty, 0)", source);
        Assert.DoesNotContain("COALESCE(rff.reserved_filled_hu_qty, 0)", source);
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

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private sealed class OrderMarkingExportPayload
    {
        [JsonPropertyName("created_code_qty")]
        public double CreatedCodeQty { get; init; }
    }
}
