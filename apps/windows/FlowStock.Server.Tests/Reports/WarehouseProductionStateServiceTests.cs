using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Reports;

public sealed class WarehouseProductionStateServiceTests
{
    [Fact]
    public void WarehouseProductionState_StockOnlyFromLedger()
    {
        var harness = CreateBaseHarness();
        SeedInternalOrder(harness, 10, 101, 1001, 120, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000001",
            PlannedQty = 60,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Filled,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 2,
            PrdDocId = 700,
            DocLineId = 7002,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000002",
            PlannedQty = 60,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(0, row.StockQty);
        Assert.Equal(120, row.PrdPlannedQty);
        Assert.Equal(60, row.PrdFilledQty);
        Assert.Contains("FILLED_PALLET_WITHOUT_LEDGER", row.Warnings);
    }

    [Fact]
    public void PlannedInternalOrder_DoesNotIncreaseStock()
    {
        var harness = CreateBaseHarness();
        SeedInternalOrder(harness, 10, 101, 1001, 80, 0, OrderStatus.InProgress);

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(0, row.StockQty);
        Assert.Equal(80, row.InternalRemainingQty);
        Assert.Equal(0, row.PrdPlannedQty);
    }

    [Fact]
    public void OpenInternalOrder_ReducesRemainingNeed()
    {
        var harness = CreateBaseHarness(minStockQty: 100);
        SeedInternalOrder(harness, 10, 101, 1001, 40, 0, OrderStatus.InProgress);

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(100, row.BelowMinQty);
        Assert.Equal(40, row.InternalRemainingQty);
        Assert.Equal(60, row.RemainingNeedQty);
    }

    [Fact]
    public void FilledPallet_AppearsAsStockViaLedger()
    {
        var harness = CreateBaseHarness(minStockQty: 0);
        SeedInternalOrder(harness, 10, 101, 1001, 100, 40, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000001",
            PlannedQty = 40,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Filled,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 2,
            PrdDocId = 700,
            DocLineId = 7002,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000002",
            PlannedQty = 60,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLedgerEntry(700, 1001, 1, 40, "HU-000001");

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));
        var filled = Assert.Single(row.ProductionReceipts.Where(pallet => pallet.HuCode == "HU-000001"));

        Assert.Equal(40, row.StockQty);
        Assert.Equal("в остатках", filled.StockEffect);
        Assert.DoesNotContain("FILLED_PALLET_WITHOUT_LEDGER", row.Warnings);
    }

    [Fact]
    public void PendingPallet_AppearsAsPlannedNotStock()
    {
        var harness = CreateBaseHarness(minStockQty: 0);
        SeedInternalOrder(harness, 10, 101, 1001, 50, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000001",
            PlannedQty = 50,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));
        var planned = Assert.Single(row.ProductionReceipts);

        Assert.Equal(0, row.StockQty);
        Assert.Equal("запланировано, не склад", planned.StockEffect);
    }

    [Fact]
    public void FullyShippedCustomerOrder_DoesNotCreateNeed()
    {
        var harness = CreateBaseHarness(minStockQty: 0);
        SeedCustomerOrder(harness, 20, 201, 1001, 90, 0, OrderStatus.Shipped, shippedQty: 90);

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: true));

        Assert.Equal(0, row.CustomerOpenDemandQty);
        Assert.Equal(0, row.CustomerRemainingToShipQty);
        Assert.Equal(0, row.RemainingNeedQty);
    }

    [Fact]
    public void ItemWithoutMinStock_NoMinStockNeed()
    {
        var harness = CreateBaseHarness(minStockQty: 0, enableMinStockControl: false);

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: true));

        Assert.Equal(0, row.MinStockQty);
        Assert.Equal(0, row.BelowMinQty);
        Assert.Equal(0, row.RemainingNeedQty);
    }

    [Fact]
    public void CustomerDemandPlusMinStockMinusInternalPlan_CalculatesRemaining()
    {
        var harness = CreateBaseHarness(minStockQty: 100);
        harness.SeedLedgerEntry(500, 1001, 1, 20);
        SeedCustomerOrder(harness, 20, 201, 1001, 30, 0, OrderStatus.InProgress);
        SeedInternalOrder(harness, 10, 101, 1001, 40, 0, OrderStatus.InProgress);

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(20, row.StockQty);
        Assert.Equal(30, row.CustomerOpenDemandQty);
        Assert.Equal(80, row.BelowMinQty);
        Assert.Equal(40, row.InternalRemainingQty);
        Assert.Equal(70, row.RemainingNeedQty);
    }

    [Fact]
    public void MixedPallet_AppearsCorrectlyInPalletSection()
    {
        var harness = CreateBaseHarness(minStockQty: 0);
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            BaseUom = "шт",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = false
        });
        SeedInternalOrder(harness, 10, 101, 1001, 40, 0, OrderStatus.InProgress);
        SeedInternalOrder(harness, 10, 102, 1002, 60, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-MIX-001",
            PlannedQty = 100,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 1,
                    ProductionPalletId = 1,
                    DocLineId = 7001,
                    OrderLineId = 101,
                    ItemId = 1001,
                    ItemName = "Горчица",
                    Uom = "шт",
                    PlannedQty = 40
                },
                new ProductionPalletComponentLine
                {
                    Id = 2,
                    ProductionPalletId = 1,
                    DocLineId = 7002,
                    OrderLineId = 102,
                    ItemId = 1002,
                    ItemName = "Кетчуп",
                    Uom = "шт",
                    PlannedQty = 60
                }
            ]
        });

        var rows = new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false);
        var mustard = Assert.Single(rows.Where(row => row.ItemId == 1001));

        var pallet = Assert.Single(mustard.ProductionReceipts);
        Assert.True(pallet.IsMixedPallet);
        Assert.Contains("Кетчуп", pallet.Composition);
        Assert.Equal(40, pallet.PlannedQty);
    }

    [Fact]
    public void WarehouseProductionState_DoesNotDoubleSubtractInternalAndPrdPlan()
    {
        var harness = CreateBaseHarness(minStockQty: 100);
        SeedInternalOrder(harness, 10, 101, 1001, 100, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000001",
            PlannedQty = 100,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(100, row.InternalRemainingQty);
        Assert.Equal(100, row.PrdPlannedQty);
        Assert.Equal(0, row.RemainingNeedQty);
    }

    [Fact]
    public async Task WarehouseProductionState_Endpoint_ReturnsExpectedShape()
    {
        var harness = CreateBaseHarness(minStockQty: 100);
        harness.SeedLedgerEntry(500, 1001, 1, 20);
        SeedCustomerOrder(harness, 20, 201, 1001, 30, 0, OrderStatus.InProgress);
        SeedInternalOrder(harness, 10, 101, 1001, 40, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000001",
            PlannedQty = 40,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/reports/warehouse-production-state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(payload.EnumerateArray());

        Assert.True(row.TryGetProperty("item_id", out _));
        Assert.True(row.TryGetProperty("stock_qty", out _));
        Assert.True(row.TryGetProperty("need_breakdown", out var needBreakdown));
        Assert.True(needBreakdown.TryGetProperty("remaining_to_create", out _));
        Assert.True(row.TryGetProperty("hu_rows", out _));
        Assert.True(row.TryGetProperty("customer_orders", out _));
        Assert.True(row.TryGetProperty("internal_orders", out _));
        Assert.True(row.TryGetProperty("production_receipts", out _));
    }

    private static CloseDocumentHarness CreateBaseHarness(double minStockQty = 1134, bool enableMinStockControl = true)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = false
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 7, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Barcode = "4660011933641",
            Gtin = "04607186951520",
            BaseUom = "шт",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = enableMinStockControl,
            MinStockQty = minStockQty,
            ItemTypeEnableMarking = false
        });
        return harness;
    }

    private static void SeedCustomerOrder(
        CloseDocumentHarness harness,
        long orderId,
        long lineId,
        long itemId,
        double qtyOrdered,
        double qtyProduced,
        OrderStatus status,
        double qtyReserved = 0,
        double shippedQty = 0)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderId.ToString(),
            Type = OrderType.Customer,
            PartnerId = 200,
            PartnerName = "Тестовый клиент",
            Status = status,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qtyOrdered,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptRemaining(orderId, new OrderReceiptLine
        {
            OrderLineId = lineId,
            OrderId = orderId,
            ItemId = itemId,
            ItemName = itemId == 1001 ? "Горчица" : "Кетчуп",
            QtyOrdered = qtyOrdered,
            QtyReceived = qtyProduced + qtyReserved,
            QtyRemaining = Math.Max(0d, qtyOrdered - qtyProduced - qtyReserved)
        });
        if (qtyReserved > 0)
        {
            harness.SeedOrderReceiptPlanLines(orderId, new OrderReceiptPlanLine
            {
                Id = lineId,
                OrderId = orderId,
                OrderLineId = lineId,
                ItemId = itemId,
                ItemName = itemId == 1001 ? "Горчица" : "Кетчуп",
                QtyPlanned = qtyReserved,
                ToLocationId = 1,
                ToLocationCode = "FG-01",
                ToHu = "HU-RES-001",
                SortOrder = 1
            });
        }

        if (shippedQty > 0)
        {
            harness.SeedShippedTotalsByOrderLine(orderId, new Dictionary<long, double>
            {
                [lineId] = shippedQty
            });
        }
    }

    private static void SeedInternalOrder(
        CloseDocumentHarness harness,
        long orderId,
        long lineId,
        long itemId,
        double qtyOrdered,
        double qtyProduced,
        OrderStatus status)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderId.ToString(),
            Type = OrderType.Internal,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qtyOrdered,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedOrderReceiptRemaining(orderId, new OrderReceiptLine
        {
            OrderLineId = lineId,
            OrderId = orderId,
            ItemId = itemId,
            ItemName = itemId == 1001 ? "Горчица" : "Кетчуп",
            QtyOrdered = qtyOrdered,
            QtyReceived = qtyProduced,
            QtyRemaining = Math.Max(0d, qtyOrdered - qtyProduced),
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
    }

    private static void SeedProductionReceiptDoc(CloseDocumentHarness harness, long docId, long orderId, string docRef)
    {
        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = docRef,
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderId.ToString(),
            CreatedAt = DateTime.UtcNow
        });
    }
}
