using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Reports;

public sealed class WarehouseProductionStateHuDisplayTests
{
    [Fact]
    public void LedgerStockAndPlannedPallet_AreSeparatedInExpandedSections()
    {
        var harness = CreateHarness();
        for (var index = 1; index <= 12; index++)
        {
            harness.SeedLedgerEntry(500, 1001, 1, 600, $"HU-{index:D7}");
        }

        SeedInternalOrder(harness, 10, 101, 1001, 7200, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 99,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-0000505",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(7200, row.StockQty, 3);
        Assert.Equal(12, row.HuRows.Count);
        Assert.All(row.HuRows, hu => Assert.Equal("На складе", hu.StockStatus));
        var planned = Assert.Single(row.ProductionReceipts);
        Assert.Equal("HU-0000505", planned.HuCode);
        Assert.Equal("Ожидает", planned.PalletStatusDisplay);
        Assert.DoesNotContain(row.ProductionReceipts, pallet => row.HuRows.Any(hu => string.Equals(hu.HuCode, pallet.HuCode, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void FilledPalletWithDraftPrdAndNoLedger_AppearsOnlyInProductionBlock()
    {
        var harness = CreateHarness();
        SeedInternalOrder(harness, 10, 101, 1001, 600, 0, OrderStatus.InProgress);
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
            HuCode = "HU-FILLED",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Filled,
            CreatedAt = DateTime.UtcNow
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(0, row.StockQty);
        Assert.Empty(row.HuRows);
        var production = Assert.Single(row.ProductionReceipts);
        Assert.Equal("HU-FILLED", production.HuCode);
        Assert.Equal("Наполнена, PRD не закрыт", production.StatusNote);
        Assert.Equal("10", production.SourceOrderRef);
    }

    [Fact]
    public void ClosedPrdLedgerPositiveHu_AppearsOnlyInWarehouseBlock()
    {
        var harness = CreateHarness();
        SeedInternalOrder(harness, 10, 101, 1001, 600, 600, OrderStatus.InProgress);
        var docId = 700L;
        harness.SeedDoc(new Doc
        {
            Id = docId,
            DocRef = "PRD-700",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 10,
            OrderRef = "10",
            CreatedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = docId,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-CLOSED",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            Status = ProductionPalletStatus.Filled,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLedgerEntry(docId, 1001, 1, 600, "HU-CLOSED");

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.Equal(600, row.StockQty, 3);
        var warehouseHu = Assert.Single(row.HuRows);
        Assert.Equal("HU-CLOSED", warehouseHu.HuCode);
        Assert.Empty(row.ProductionReceipts);
    }

    [Fact]
    public void ReservedLedgerHu_ShowsReservedCustomerOrderInWarehouseBlock()
    {
        var harness = CreateHarness();
        harness.SeedLedgerEntry(500, 1001, 1, 600, "HU-RESERVED");
        SeedCustomerOrder(harness, 20, 201, 1001, 600, 0, OrderStatus.InProgress);
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            Id = 900,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 1001,
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-RESERVED",
            SortOrder = 0
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));
        var warehouseHu = Assert.Single(row.HuRows);

        Assert.Equal("Зарезервирован: заказ 20", warehouseHu.StockStatus);
    }

    [Fact]
    public void CancelledPallet_IsNotShownInProductionBlock()
    {
        var harness = CreateHarness();
        SeedInternalOrder(harness, 10, 101, 1001, 600, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            HuCode = "HU-CANCELLED",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Cancelled,
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
            HuCode = "HU-PLANNED",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = DateTime.UtcNow
        });

        var row = Assert.Single(new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false));

        Assert.DoesNotContain(row.ProductionReceipts, pallet => string.Equals(pallet.HuCode, "HU-CANCELLED", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(row.ProductionReceipts, pallet => string.Equals(pallet.HuCode, "HU-PLANNED", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MixedHu_ShowsItemSpecificQtyInWarehouseAndProductionBlocks()
    {
        var harness = CreateHarness();
        harness.SeedLedgerEntry(500, 1001, 1, 100, "HU-MIXED");
        harness.SeedLedgerEntry(501, 1002, 1, 200, "HU-MIXED");
        SeedInternalOrder(harness, 10, 101, 1001, 300, 0, OrderStatus.InProgress);
        SeedProductionReceiptDoc(harness, 700, 10, "PRD-700");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 700,
            DocLineId = 7001,
            OrderId = 10,
            ItemId = 1001,
            HuCode = "HU-MIXED",
            PlannedQty = 300,
            Status = ProductionPalletStatus.Planned,
            Lines =
            [
                new ProductionPalletComponentLine { Id = 11, ProductionPalletId = 1, DocLineId = 7001, ItemId = 1001, ItemName = "A", PlannedQty = 100 },
                new ProductionPalletComponentLine { Id = 12, ProductionPalletId = 1, DocLineId = 7002, ItemId = 1002, ItemName = "B", PlannedQty = 200 }
            ],
            CreatedAt = DateTime.UtcNow
        });

        var rows = new WarehouseProductionStateService(harness.Store).GetRows(includeZero: false)
            .ToDictionary(row => row.ItemId);

        Assert.Equal(100, Assert.Single(rows[1001].HuRows).Qty, 3);
        Assert.Equal(200, Assert.Single(rows[1002].HuRows).Qty, 3);
        Assert.Equal(100, Assert.Single(rows[1001].ProductionReceipts).Qty, 3);
        Assert.Equal(200, Assert.Single(rows[1002].ProductionReceipts).Qty, 3);
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "FG-01", Name = "Склад" });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            BaseUom = "шт",
            ItemTypeName = "Готовая продукция"
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            BaseUom = "шт",
            ItemTypeName = "Готовая продукция"
        });
        return harness;
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
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qtyOrdered
        });
        harness.SeedOrderReceiptRemaining(orderId, new OrderReceiptLine
        {
            OrderLineId = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qtyOrdered,
            QtyReceived = qtyProduced,
            QtyRemaining = Math.Max(0, qtyOrdered - qtyProduced)
        });
    }

    private static void SeedCustomerOrder(
        CloseDocumentHarness harness,
        long orderId,
        long lineId,
        long itemId,
        double qtyOrdered,
        double shippedQty,
        OrderStatus status)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderId.ToString(),
            Type = OrderType.Customer,
            Status = status,
            UseReservedStock = true,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qtyOrdered
        });
        if (shippedQty > 0)
        {
            harness.SeedShippedTotalsByOrderLine(orderId, new Dictionary<long, double> { [lineId] = shippedQty });
        }
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
