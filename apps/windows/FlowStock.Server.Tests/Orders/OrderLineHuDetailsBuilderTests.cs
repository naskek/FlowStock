using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineHuDetailsBuilderTests
{
    [Fact]
    public async Task SingleEndpoint_InternalProducedHuIncludesLaterCustomerShipmentFateWithoutChangingLineShipment()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 3,
            OrderRef = "003",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 30, OrderId = 3, ItemId = 5, QtyOrdered = 1824 });
        harness.SeedOrder(new Order
        {
            Id = 4,
            OrderRef = "004",
            Type = OrderType.Customer,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 40, OrderId = 4, ItemId = 5, QtyOrdered = 1824 });
        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-2026-000012",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 3,
            CreatedAt = new DateTime(2026, 6, 10, 10, 0, 0),
            ClosedAt = new DateTime(2026, 6, 10, 11, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 101,
            PrdDocId = 100,
            OrderId = 3,
            OrderLineId = 30,
            ItemId = 5,
            HuCode = "HU-0002083",
            PlannedQty = 1824,
            Status = ProductionPalletStatus.Filled,
            CreatedAt = new DateTime(2026, 6, 10, 10, 0, 0),
            FilledAt = new DateTime(2026, 6, 10, 11, 0, 0)
        });
        harness.SeedLedgerEntry(100, 5, 1, 1824, "HU-0002083");
        harness.SeedDoc(new Doc
        {
            Id = 200,
            DocRef = "OUT-2026-000004",
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = 4,
            CreatedAt = new DateTime(2026, 6, 11, 8, 0, 0),
            ClosedAt = new DateTime(2026, 6, 11, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 200,
            OrderLineId = 40,
            ItemId = 5,
            Qty = 1824,
            FromHu = "HU-0002083"
        });
        harness.SeedLedgerEntry(200, 5, 1, -1824, "HU-0002083");

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        using var response = await host.Client.GetAsync("/api/orders/3/lines");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var line = Assert.Single(json.RootElement.EnumerateArray());
        var productionHu = Assert.Single(line.GetProperty("production_hu_rows").EnumerateArray());
        Assert.Equal("HU-0002083", productionHu.GetProperty("hu_code").GetString());
        Assert.Equal("SHIPPED", productionHu.GetProperty("fate_code").GetString());
        Assert.Equal("→ отгружено заказ 004", productionHu.GetProperty("fate_label").GetString());
        Assert.Equal("004", productionHu.GetProperty("fate_order_ref").GetString());
        Assert.Equal("OUT-2026-000004", productionHu.GetProperty("fate_doc_ref").GetString());
        Assert.Equal(1824, productionHu.GetProperty("fate_qty").GetDouble(), 3);
        Assert.Empty(line.GetProperty("shipped_hu_rows").EnumerateArray());
        Assert.Equal(0, line.GetProperty("coverage").GetProperty("shipped_qty").GetDouble(), 3);
        Assert.Equal(1824, line.GetProperty("coverage").GetProperty("covered_qty").GetDouble(), 3);
        Assert.Equal(0, line.GetProperty("coverage").GetProperty("missing_qty").GetDouble(), 3);
    }

    [Fact]
    public void BuildByOrder_CustomerWithNoHuCoverageReportsExactPositiveMissingQty()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 5,
            OrderRef = "005",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine { Id = 50, OrderId = 5, ItemId = 5, QtyOrdered = 100 });

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(5));
        var details = OrderLineHuDetailsBuilder.BuildByOrder(harness.Store, order, [line])[50];

        Assert.NotNull(details.Coverage);
        Assert.Equal(0, details.Coverage.CoveredQty, 3);
        Assert.Equal(100, details.Coverage.MissingQty, 3);
    }

    [Fact]
    public void BuildByOrder_CustomerUsesActualBoundProductionAndShippedHuWithoutDoubleCountingCoverage()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 10,
            OrderRef = "010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine { Id = 100, OrderId = 10, ItemId = 5, QtyOrdered = 100 });
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 5,
            QtyPlanned = 100,
            ToHu = "HU-100",
            ToLocationId = 1
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-20",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0),
            ClosedAt = new DateTime(2026, 6, 10, 10, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 21,
            PrdDocId = 20,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 5,
            HuCode = "HU-100",
            PlannedQty = 100,
            Status = ProductionPalletStatus.Filled,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0),
            FilledAt = new DateTime(2026, 6, 10, 10, 0, 0)
        });
        harness.SeedLedgerEntry(20, 5, 1, 100, "HU-100");
        harness.SeedDoc(new Doc
        {
            Id = 30,
            DocRef = "OUT-30",
            Type = DocType.Outbound,
            Status = DocStatus.Closed,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 6, 10, 11, 0, 0),
            ClosedAt = new DateTime(2026, 6, 10, 12, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 31,
            DocId = 30,
            OrderLineId = 100,
            ItemId = 5,
            Qty = 20,
            FromHu = "HU-100"
        });
        harness.SeedLedgerEntry(30, 5, 1, -20, "HU-100");
        harness.SeedShippedTotalsByOrderLine(10, new Dictionary<long, double> { [100] = 20 });

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(10));
        var details = OrderLineHuDetailsBuilder.BuildByOrder(harness.Store, order, [line])[100];

        var warehouse = Assert.Single(details.WarehouseHuRows);
        Assert.Equal("HU-100", warehouse.HuCode);
        Assert.Equal(80, warehouse.Qty, 3);
        Assert.Equal("MAIN", warehouse.LocationCode);
        Assert.True(warehouse.IsBoundToOrder);

        var production = Assert.Single(details.ProductionHuRows);
        Assert.Equal("HU-100", production.HuCode);
        Assert.Equal("PRD-20", production.PrdRef);
        Assert.Equal(100, production.FilledQty, 3);

        var shipped = Assert.Single(details.ShippedHuRows);
        Assert.Equal("HU-100", shipped.HuCode);
        Assert.Equal(20, shipped.Qty, 3);

        Assert.NotNull(details.Coverage);
        Assert.Equal(100, details.Coverage.CoveredQty, 3);
        Assert.Equal(0, details.Coverage.MissingQty, 3);
        Assert.Equal(80, details.Coverage.WarehouseBoundQty, 3);
        Assert.Equal(100, details.Coverage.ProductionFilledQty, 3);
        Assert.Equal(20, details.Coverage.ShippedQty, 3);
    }

    [Fact]
    public void BuildByOrder_InternalUsesExistingProducedMetricAndDoesNotExposeCustomerWarehouseBinding()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 20,
            OrderRef = "020",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine { Id = 200, OrderId = 20, ItemId = 5, QtyOrdered = 100 });
        harness.SeedOrderReceiptRemaining(20, new OrderReceiptLine
        {
            OrderId = 20,
            OrderLineId = 200,
            ItemId = 5,
            QtyOrdered = 100,
            QtyReceived = 40,
            QtyRemaining = 60
        });

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(20));
        var details = OrderLineHuDetailsBuilder.BuildByOrder(harness.Store, order, [line])[200];

        Assert.Empty(details.WarehouseHuRows);
        Assert.NotNull(details.Coverage);
        Assert.Equal(40, details.Coverage.CoveredQty, 3);
        Assert.Equal(60, details.Coverage.MissingQty, 3);
        Assert.Equal(0, details.Coverage.ShippedQty, 3);
    }
}
