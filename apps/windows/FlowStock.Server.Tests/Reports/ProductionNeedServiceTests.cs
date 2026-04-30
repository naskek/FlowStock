using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Reports;

public sealed class ProductionNeedServiceTests
{
    [Fact]
    public void ProductionNeed_WhenPhysical3648Active1824Min5472_Returns3648()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 3648,
            activeCustomerOrderOpenQty: 1824,
            minStockQty: 5472,
            reservedCustomerOrderQty: 0,
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(3648, row.PhysicalStockQty);
        Assert.Equal(1824, row.ActiveCustomerOrderOpenQty);
        Assert.Equal(5472, row.MinStockQty);
        Assert.Equal(3648, row.ProductionNeedQty);
    }

    [Fact]
    public void ProductionNeed_WhenPhysical5472Active1824Min5472_Returns1824()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 5472,
            activeCustomerOrderOpenQty: 1824,
            minStockQty: 5472,
            reservedCustomerOrderQty: 0,
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(1824, row.ProductionNeedQty);
    }

    [Fact]
    public void ProductionNeed_WhenPhysical378Active1512Min1134_Returns2268()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 378,
            activeCustomerOrderOpenQty: 1512,
            minStockQty: 1134,
            reservedCustomerOrderQty: 0,
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(2268, row.ProductionNeedQty);
    }

    [Fact]
    public void ShippedOrders_DoNotCreateProductionNeed()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetItems(null)).Returns([
            new Item
            {
                Id = 10,
                Name = "Товар",
                ItemTypeEnableMinStockControl = false
            }
        ]);
        store.Setup(s => s.GetStock(null)).Returns(Array.Empty<StockRow>());
        store.Setup(s => s.GetOrders()).Returns([
            new Order
            {
                Id = 1,
                OrderRef = "001",
                Type = OrderType.Customer,
                Status = OrderStatus.Shipped
            }
        ]);

        var row = new ProductionNeedService(store.Object).GetRows(includeZeroNeed: true).Single();

        Assert.Equal(0, row.ActiveCustomerOrderOpenQty);
        Assert.Equal(0, row.ProductionNeedQty);
        store.Verify(s => s.GetItems(null), Times.Once);
        store.Verify(s => s.GetStock(null), Times.Once);
        store.Verify(s => s.GetOrders(), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void CancelledOrders_DoNotCreateProductionNeed()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetItems(null)).Returns([
            new Item
            {
                Id = 10,
                Name = "Товар",
                ItemTypeEnableMinStockControl = false
            }
        ]);
        store.Setup(s => s.GetStock(null)).Returns(Array.Empty<StockRow>());
        store.Setup(s => s.GetOrders()).Returns([
            new Order
            {
                Id = 1,
                OrderRef = "001",
                Type = OrderType.Customer,
                Status = OrderStatus.Cancelled
            }
        ]);

        var row = new ProductionNeedService(store.Object).GetRows(includeZeroNeed: true).Single();

        Assert.Equal(0, row.ActiveCustomerOrderOpenQty);
        Assert.Equal(0, row.ProductionNeedQty);
        store.Verify(s => s.GetItems(null), Times.Once);
        store.Verify(s => s.GetStock(null), Times.Once);
        store.Verify(s => s.GetOrders(), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void PartialReserve_DoesNotReduceActiveCustomerOrderOpenQty()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 1000,
            activeCustomerOrderOpenQty: 1512,
            minStockQty: 0,
            reservedCustomerOrderQty: 500,
            store: out _,
            splitStockRows: true);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(1512, row.ActiveCustomerOrderOpenQty);
        Assert.Equal(500, row.ReservedCustomerOrderQty);
        Assert.Equal(500, row.FreeStockQty);
        Assert.Equal(512, row.ProductionNeedQty);
    }

    [Fact]
    public void Report_IsReadOnlyAndDoesNotMutateLedger()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 3648,
            activeCustomerOrderOpenQty: 1824,
            minStockQty: 5472,
            reservedCustomerOrderQty: 250,
            store: out var store,
            splitStockRows: true);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(3648, row.PhysicalStockQty);
        Assert.Equal(250, row.ReservedCustomerOrderQty);
        store.Verify(s => s.GetItems(null), Times.Once);
        store.Verify(s => s.GetStock(null), Times.Once);
        store.Verify(s => s.GetOrders(), Times.Once);
        store.Verify(s => s.GetShippedTotalsByOrderLine(1), Times.Once);
        store.Verify(s => s.GetOrderLines(1), Times.Once);
        store.VerifyNoOtherCalls();
    }

    private static ProductionNeedService BuildService(
        long itemId,
        double physicalStockQty,
        double activeCustomerOrderOpenQty,
        double minStockQty,
        double reservedCustomerOrderQty,
        out Mock<IDataStore> store,
        bool splitStockRows = false)
    {
        store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetItems(null)).Returns([
            new Item
            {
                Id = itemId,
                Name = "Товар",
                ItemTypeName = "Готовая продукция",
                ItemTypeEnableMinStockControl = true,
                MinStockQty = minStockQty
            }
        ]);
        store.Setup(s => s.GetStock(null)).Returns(BuildStockRows(itemId, physicalStockQty, reservedCustomerOrderQty, splitStockRows));
        store.Setup(s => s.GetOrders()).Returns([
            new Order
            {
                Id = 1,
                OrderRef = "001",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress
            }
        ]);
        store.Setup(s => s.GetOrderLines(1)).Returns([
            new OrderLine
            {
                Id = 100,
                OrderId = 1,
                ItemId = itemId,
                QtyOrdered = activeCustomerOrderOpenQty
            }
        ]);
        store.Setup(s => s.GetShippedTotalsByOrderLine(1)).Returns(new Dictionary<long, double>());

        return new ProductionNeedService(store.Object);
    }

    private static IReadOnlyList<StockRow> BuildStockRows(long itemId, double physicalStockQty, double reservedCustomerOrderQty, bool splitStockRows)
    {
        if (!splitStockRows)
        {
            return [
                new StockRow
                {
                    ItemId = itemId,
                    ItemName = "Товар",
                    LocationCode = "A-01",
                    Qty = physicalStockQty,
                    ReservedCustomerOrderQty = reservedCustomerOrderQty
                }
            ];
        }

        var firstQty = Math.Round(physicalStockQty / 2, 3, MidpointRounding.AwayFromZero);
        var secondQty = physicalStockQty - firstQty;
        return [
            new StockRow
            {
                ItemId = itemId,
                ItemName = "Товар",
                LocationCode = "A-01",
                Hu = "HU-001",
                Qty = firstQty,
                ReservedCustomerOrderQty = reservedCustomerOrderQty
            },
            new StockRow
            {
                ItemId = itemId,
                ItemName = "Товар",
                LocationCode = "A-02",
                Hu = "HU-002",
                Qty = secondQty,
                ReservedCustomerOrderQty = reservedCustomerOrderQty
            }
        ];
    }
}
