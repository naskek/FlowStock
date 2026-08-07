using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineHuDetailsBuilderTests
{
    [Fact]
    public async Task SingleEndpoint_ReturnsCanonicalHuPresentationAndSuppressesPlannedRows()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 2,
            OrderRef = "002",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 8, 7, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 20, OrderId = 2, ItemId = 5, QtyOrdered = 200 });
        harness.SeedHuOperatorFactsForOrder(2,
        [
            OperatorProductionFacts("HU-PLANNED", ProductionPalletStatus.Planned),
            OperatorProductionFacts("HU-PRINTED", ProductionPalletStatus.Printed)
        ]);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        using var response = await host.Client.GetAsync("/api/orders/2/lines");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var line = Assert.Single(json.RootElement.EnumerateArray());
        var presentation = line.GetProperty("hu_presentation");
        var production = Assert.Single(presentation.GetProperty("production_tasks").EnumerateArray());
        Assert.Equal("HU-PRINTED", production.GetProperty("hu_code").GetString());
        Assert.Equal("AWAITING_FILL", production.GetProperty("state").GetProperty("code").GetString());
        Assert.False(production.TryGetProperty("components", out _));
        Assert.False(production.TryGetProperty("planned_qty", out _));
        Assert.False(production.TryGetProperty("filled_qty", out _));
        Assert.Empty(presentation.GetProperty("operational_hus").EnumerateArray());

        static HuOperatorFacts OperatorProductionFacts(string huCode, string status) => new()
        {
            HuCode = huCode,
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = string.Equals(status, ProductionPalletStatus.Planned, StringComparison.Ordinal) ? 1 : 2,
                    Status = status,
                    OwnerOrderId = 2,
                    OwnerOrderRef = "002",
                    OwnerOrderType = "CUSTOMER",
                    OwnerOrderStatus = "IN_PROGRESS",
                    Components =
                    [
                        new HuOperatorComponentFact
                        {
                            OrderLineId = 20,
                            OrderLineOrderId = 2,
                            ItemId = 5,
                            ItemName = "Товар",
                            Uom = "шт",
                            PlannedQty = 100
                        }
                    ]
                }
            ]
        };
    }

    [Fact]
    public async Task SingleEndpoint_KeepsInconsistentWarehouseHuWithNullableLineQuantity()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 2,
            OrderRef = "002",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 8, 7, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 20, OrderId = 2, ItemId = 5, QtyOrdered = 100 });
        harness.SeedHuOperatorFactsForOrder(2,
        [
            new HuOperatorFacts
            {
                HuCode = "HU-PARTIAL-OUT",
                Stock =
                [
                    new HuOperatorStockFact
                    {
                        ItemId = 5,
                        ItemName = "Товар",
                        Uom = "шт",
                        LocationId = 10,
                        LocationCode = "MAIN",
                        Qty = 60
                    }
                ],
                Outbound =
                [
                    new HuOperatorOutboundFact
                    {
                        DocumentId = 91,
                        DocumentRef = "OUT-91",
                        DocumentStatus = "CLOSED",
                        OrderId = 2,
                        OrderRef = "002",
                        OrderLineId = 20,
                        ItemId = 5,
                        ItemName = "Товар",
                        Uom = "шт",
                        Qty = 40
                    }
                ],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", 100),
                    Movement(2, 91, "OUTBOUND", -40)
                ]
            }
        ]);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        using var response = await host.Client.GetAsync("/api/orders/2/lines");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var row = Assert.Single(
            Assert.Single(json.RootElement.EnumerateArray())
                .GetProperty("hu_presentation")
                .GetProperty("operational_hus")
                .EnumerateArray());
        Assert.Equal("INCONSISTENT", row.GetProperty("state").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("qty").ValueKind);

        static HuOperatorLedgerMovementFact Movement(
            long ledgerId,
            long documentId,
            string documentType,
            double qtyDelta) => new()
        {
            LedgerId = ledgerId,
            Timestamp = new DateTime(2026, 8, 7, 8, 0, 0).AddMinutes(ledgerId),
            DocumentId = documentId,
            DocumentRef = $"DOC-{documentId}",
            DocumentType = documentType,
            DocumentStatus = "CLOSED",
            ItemId = 5,
            ItemName = "Товар",
            Uom = "шт",
            LocationId = 10,
            LocationCode = "MAIN",
            QtyDelta = qtyDelta
        };
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(3L, false)]
    [InlineData(2L, true)]
    public void BuildByOrder_CustomerFilledHuWithLedger_RequiresExplicitMatchingPalletOwner(
        long? productionPalletOrderId,
        bool expectedAwaitingShipment)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 2,
            OrderRef = "002",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine
        {
            Id = 20,
            OrderId = 2,
            ItemId = 5,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-100",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 2,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 101,
            PrdDocId = 100,
            OrderId = productionPalletOrderId,
            OrderLineId = 20,
            ItemId = 5,
            HuCode = "HU-CUSTOMER",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 6, 10, 10, 0, 0),
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 102,
            PrdDocId = 100,
            OrderId = 2,
            OrderLineId = 20,
            ItemId = 5,
            HuCode = "HU-PRINTED",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Printed,
            CreatedAt = new DateTime(2026, 6, 10, 9, 5, 0)
        });
        harness.SeedLedgerEntry(100, 5, 1, 600, "HU-CUSTOMER");

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(2));
        var details = OrderLineHuDetailsBuilder.BuildByOrder(harness.Store, order, [line])[20];

        Assert.Equal(2, details.ProductionHuRows.Count);
        var ready = details.ProductionHuRows.Single(row => row.HuCode == "HU-CUSTOMER");
        Assert.Equal(ProductionPalletStatus.Filled, ready.PalletStatus);
        Assert.Equal(
            expectedAwaitingShipment
                ? OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode
                : OrderLineHuFateDisplayBuilder.OnStockFateCode,
            ready.FateCode);
        Assert.Equal(
            expectedAwaitingShipment
                ? OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel
                : "на складе",
            ready.FateLabel);
        Assert.Equal(600, ready.FateQty);
        var printed = details.ProductionHuRows.Single(row => row.HuCode == "HU-PRINTED");
        Assert.Equal(ProductionPalletStatus.Printed, printed.PalletStatus);
        Assert.Null(printed.FateCode);
        harness.VerifyNoGlobalHuFateReads();
        harness.VerifyScopedHuFateLookup(Moq.Times.Once());
    }

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
        harness.VerifyNoGlobalHuFateReads();
        harness.VerifyScopedHuFateLookup(Moq.Times.Once());
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
    public void BuildByOrder_NoProductionHuRows_SkipsFateAndPreservesCoverage()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 6,
            OrderRef = "006",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine { Id = 60, OrderId = 6, ItemId = 5, QtyOrdered = 100 });

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(6));
        var detailsTiming = new OrderLineHuDetailsTiming();
        var fateTiming = new OrderLineHuFateTiming();
        var details = OrderLineHuDetailsBuilder.BuildByOrder(
            harness.Store,
            order,
            [line],
            detailsTiming,
            fateTiming)[60];

        Assert.Empty(details.ProductionHuRows);
        Assert.NotNull(details.Coverage);
        Assert.Equal(0, details.Coverage.CoveredQty, 3);
        Assert.Equal(100, details.Coverage.MissingQty, 3);
        Assert.Equal(0, detailsTiming.HuFateMs);
        Assert.True(fateTiming.Skipped);
        Assert.Equal(0, fateTiming.GetOrdersMs);
        Assert.Equal(0, fateTiming.GetDocsMs);
        Assert.Equal(0, fateTiming.BuildSourcesMs);
        Assert.Equal(0, fateTiming.BuildShipmentsMs);
        Assert.Equal(0, fateTiming.FinalRowsCount);
        Assert.Equal(0, fateTiming.TotalMs);
        harness.VerifyNoGlobalHuFateReads();
        harness.VerifyScopedHuFateLookup(Moq.Times.Never());
    }

    [Theory]
    [InlineData(ProductionPalletStatus.Planned)]
    [InlineData(ProductionPalletStatus.Printed)]
    public void BuildByOrder_CustomerUnfilledProductionHuRemainsVisibleWithoutScopedFate(string palletStatus)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 7,
            OrderRef = "007",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine { Id = 70, OrderId = 7, ItemId = 5, QtyOrdered = 100 });
        harness.SeedDoc(new Doc
        {
            Id = 71,
            DocRef = "PRD-71",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 7,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 72,
            PrdDocId = 71,
            OrderId = 7,
            OrderLineId = 70,
            ItemId = 5,
            HuCode = "HU-PLANNED",
            PlannedQty = 100,
            Status = palletStatus,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
        });

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(7));
        var detailsTiming = new OrderLineHuDetailsTiming();
        var fateTiming = new OrderLineHuFateTiming();
        var details = OrderLineHuDetailsBuilder.BuildByOrder(
            harness.Store,
            order,
            [line],
            detailsTiming,
            fateTiming)[70];

        var production = Assert.Single(details.ProductionHuRows);
        Assert.Equal("HU-PLANNED", production.HuCode);
        Assert.Null(production.FateCode);
        Assert.Null(production.FateLabel);
        Assert.Equal(0, detailsTiming.HuFateMs);
        Assert.True(fateTiming.Skipped);
        Assert.True(fateTiming.Scoped);
        Assert.Equal(0, fateTiming.ScopedKeysCount);
        harness.VerifyNoGlobalHuFateReads();
        harness.VerifyScopedHuFateLookup(Moq.Times.Never());
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
    public void BuildByOrder_CustomerFilledPalletWithoutLedgerDoesNotCountAsReadyCoverage()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 5, Name = "Товар", BaseUom = "шт" });
        var order = new Order
        {
            Id = 11,
            OrderRef = "011",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 10, 8, 0, 0)
        };
        harness.SeedOrder(order);
        harness.SeedOrderLine(new OrderLine { Id = 110, OrderId = 11, ItemId = 5, QtyOrdered = 1800 });
        harness.SeedDoc(new Doc
        {
            Id = 210,
            DocRef = "PRD-210",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 11,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 211,
            PrdDocId = 210,
            OrderId = 11,
            OrderLineId = 110,
            ItemId = 5,
            HuCode = "HU-NO-LEDGER",
            PlannedQty = 1800,
            Status = ProductionPalletStatus.Filled,
            CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0),
            FilledAt = new DateTime(2026, 6, 10, 10, 0, 0)
        });

        var line = Assert.Single(new OrderService(harness.Store).GetOrderLineViews(11));
        var details = OrderLineHuDetailsBuilder.BuildByOrder(harness.Store, order, [line])[110];

        var production = Assert.Single(details.ProductionHuRows);
        Assert.Null(production.FateCode);
        Assert.Null(production.FateLabel);
        Assert.NotNull(details.Coverage);
        Assert.Equal(0, details.Coverage.CoveredQty, 3);
        Assert.Equal(1800, details.Coverage.MissingQty, 3);
        Assert.Equal(0, line.QtyProduced, 3);
        Assert.Equal(1800, line.Shortage, 3);
        Assert.Equal(0, line.CanShipNow, 3);
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
