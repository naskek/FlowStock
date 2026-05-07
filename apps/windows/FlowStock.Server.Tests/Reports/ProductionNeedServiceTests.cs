using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Reports;

public sealed class ProductionNeedServiceTests
{
    [Fact]
    public void ProductionNeed_SplitsOrdersAndMinStock_AsRequested()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 1134,
            minStockQty: 1134,
            orderScenarios:
            [
                new OrderScenario(
                    OrderId: 1,
                    LineId: 100,
                    QtyOrdered: 1890,
                    QtyReserved: 1134,
                    DueDate: DateTime.Today)
            ],
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(0, row.FreeStockQty);
        Assert.Equal(1134, row.MinStockQty);
        Assert.Equal(756, row.ToCloseOrdersQty);
        Assert.Equal(1134, row.ToMinStockQty);
        Assert.Equal(1890, row.TotalToMakeQty);
    }

    [Fact]
    public void ProductionNeed_WithoutOrders_UsesOnlyMinStock()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 0,
            minStockQty: 1134,
            orderScenarios: Array.Empty<OrderScenario>(),
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(0, row.ToCloseOrdersQty);
        Assert.Equal(1134, row.ToMinStockQty);
        Assert.Equal(1134, row.TotalToMakeQty);
    }

    [Fact]
    public void ProductionNeed_WhenOrdersCoveredAndFreeStockAboveMin_ReturnsZero()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 3000,
            minStockQty: 1134,
            orderScenarios:
            [
                new OrderScenario(
                    OrderId: 1,
                    LineId: 100,
                    QtyOrdered: 1000,
                    QtyReserved: 1000,
                    DueDate: DateTime.Today)
            ],
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(2000, row.FreeStockQty);
        Assert.Equal(0, row.ToCloseOrdersQty);
        Assert.Equal(0, row.ToMinStockQty);
        Assert.Equal(0, row.TotalToMakeQty);
    }

    [Fact]
    public void ProductionNeed_AggregatesCurrentNeed_WithoutDateSplit()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 50,
            minStockQty: 200,
            orderScenarios:
            [
                new OrderScenario(
                    OrderId: 1,
                    LineId: 100,
                    QtyOrdered: 100,
                    QtyReserved: 40,
                    DueDate: tomorrow)
            ],
            store: out var store);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(DateTime.Today, row.NeedDate);
        Assert.Equal(60, row.ToCloseOrdersQty);
        Assert.Equal(190, row.ToMinStockQty);
        Assert.Equal(250, row.TotalToMakeQty);

        store.Verify(s => s.GetItems(null), Times.Once);
        store.Verify(s => s.GetStock(null), Times.Once);
        store.Verify(s => s.GetOrders(), Times.Exactly(2));
        store.Verify(s => s.GetShippedTotalsByOrderLine(1), Times.Once);
        store.Verify(s => s.GetOrderReceiptPlanLines(1), Times.Once);
        store.Verify(s => s.GetOrderLines(1), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void PlannedProduction_CoversOrdersThenMinStock_WhenEnoughForAll()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 0,
            minStockQty: 1134,
            orderScenarios:
            [
                new OrderScenario(
                    OrderId: 1,
                    LineId: 100,
                    QtyOrdered: 756,
                    QtyReserved: 0,
                    DueDate: DateTime.Today)
            ],
            plannedScenarios:
            [
                new PlannedScenario(
                    OrderId: 2,
                    LineId: 200,
                    QtyRemaining: 1890,
                    Purpose: ProductionLinePurpose.InternalStock)
            ],
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(0, row.ToCloseOrdersQty);
        Assert.Equal(0, row.ToMinStockQty);
        Assert.Equal(0, row.TotalToMakeQty);
    }

    [Fact]
    public void PlannedProduction_CoversOrdersFirst_WhenPlannedQtyIs500()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 0,
            minStockQty: 1134,
            orderScenarios:
            [
                new OrderScenario(
                    OrderId: 1,
                    LineId: 100,
                    QtyOrdered: 756,
                    QtyReserved: 0,
                    DueDate: DateTime.Today)
            ],
            plannedScenarios:
            [
                new PlannedScenario(
                    OrderId: 2,
                    LineId: 200,
                    QtyRemaining: 500,
                    Purpose: ProductionLinePurpose.InternalStock)
            ],
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(256, row.ToCloseOrdersQty);
        Assert.Equal(1134, row.ToMinStockQty);
        Assert.Equal(1390, row.TotalToMakeQty);
    }

    [Fact]
    public void PlannedProduction_CoversOrdersFirst_WhenPlannedQtyIs1000()
    {
        var service = BuildService(
            itemId: 10,
            physicalStockQty: 0,
            minStockQty: 1134,
            orderScenarios:
            [
                new OrderScenario(
                    OrderId: 1,
                    LineId: 100,
                    QtyOrdered: 756,
                    QtyReserved: 0,
                    DueDate: DateTime.Today)
            ],
            plannedScenarios:
            [
                new PlannedScenario(
                    OrderId: 2,
                    LineId: 200,
                    QtyRemaining: 1000,
                    Purpose: ProductionLinePurpose.InternalStock)
            ],
            store: out _);

        var row = service.GetRows(includeZeroNeed: true).Single();

        Assert.Equal(0, row.ToCloseOrdersQty);
        Assert.Equal(890, row.ToMinStockQty);
        Assert.Equal(890, row.TotalToMakeQty);
    }

    private static ProductionNeedService BuildService(
        long itemId,
        double physicalStockQty,
        double minStockQty,
        IReadOnlyList<OrderScenario> orderScenarios,
        IReadOnlyList<PlannedScenario>? plannedScenarios,
        out Mock<IDataStore> store)
    {
        var reservedCustomerOrderQty = orderScenarios.Sum(scenario => scenario.QtyReserved);
        store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetItems(null)).Returns(
        [
            new Item
            {
                Id = itemId,
                Name = "Товар",
                Gtin = "04607186951520",
                ItemTypeName = "Готовая продукция",
                ItemTypeEnableMinStockControl = true,
                MinStockQty = minStockQty
            }
        ]);
        store.Setup(s => s.GetStock(null)).Returns(
        [
            new StockRow
            {
                ItemId = itemId,
                ItemName = "Товар",
                LocationCode = "A-01",
                Qty = physicalStockQty,
                ReservedCustomerOrderQty = reservedCustomerOrderQty
            }
        ]);

        var planned = plannedScenarios ?? Array.Empty<PlannedScenario>();
        var orders = orderScenarios
            .Select(scenario => new Order
            {
                Id = scenario.OrderId,
                OrderRef = scenario.OrderId.ToString(),
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress,
                DueDate = scenario.DueDate
            })
            .Concat(planned.Select(scenario => new Order
            {
                Id = scenario.OrderId,
                OrderRef = scenario.OrderId.ToString(),
                Type = scenario.OrderType,
                Status = scenario.Status
            }))
            .Cast<Order>()
            .ToArray();
        store.Setup(s => s.GetOrders()).Returns(orders);

        foreach (var scenario in orderScenarios)
        {
            store.Setup(s => s.GetOrderLines(scenario.OrderId)).Returns(
            [
                new OrderLine
                {
                    Id = scenario.LineId,
                    OrderId = scenario.OrderId,
                    ItemId = itemId,
                    QtyOrdered = scenario.QtyOrdered
                }
            ]);
            store.Setup(s => s.GetShippedTotalsByOrderLine(scenario.OrderId)).Returns(
                new Dictionary<long, double> { [scenario.LineId] = scenario.QtyShipped });

            IReadOnlyList<OrderReceiptPlanLine> planLines = scenario.QtyReserved > 0
                ?
                [
                    new OrderReceiptPlanLine
                    {
                        Id = scenario.LineId + 1000,
                        OrderId = scenario.OrderId,
                        OrderLineId = scenario.LineId,
                        ItemId = itemId,
                        ItemName = "Товар",
                        QtyPlanned = scenario.QtyReserved,
                        SortOrder = 1
                    }
                ]
                : Array.Empty<OrderReceiptPlanLine>();
            store.Setup(s => s.GetOrderReceiptPlanLines(scenario.OrderId)).Returns(planLines);
        }

        foreach (var scenario in planned)
        {
            store.Setup(s => s.GetOrderLines(scenario.OrderId)).Returns(
            [
                new OrderLine
                {
                    Id = scenario.LineId,
                    OrderId = scenario.OrderId,
                    ItemId = itemId,
                    QtyOrdered = scenario.QtyRemaining,
                    ProductionPurpose = scenario.Purpose
                }
            ]);
            store.Setup(s => s.GetOrderReceiptRemaining(scenario.OrderId)).Returns(
            [
                new OrderReceiptLine
                {
                    OrderLineId = scenario.LineId,
                    OrderId = scenario.OrderId,
                    ItemId = itemId,
                    ItemName = "Товар",
                    QtyOrdered = scenario.QtyRemaining,
                    QtyReceived = 0,
                    QtyRemaining = scenario.QtyRemaining,
                    ProductionPurpose = scenario.Purpose
                }
            ]);
        }

        return new ProductionNeedService(store.Object);
    }

    private static ProductionNeedService BuildService(
        long itemId,
        double physicalStockQty,
        double minStockQty,
        IReadOnlyList<OrderScenario> orderScenarios,
        out Mock<IDataStore> store)
    {
        return BuildService(itemId, physicalStockQty, minStockQty, orderScenarios, null, out store);
    }

    private sealed record OrderScenario(
        long OrderId,
        long LineId,
        double QtyOrdered,
        double QtyReserved,
        DateTime? DueDate,
        double QtyShipped = 0);

    private sealed record PlannedScenario(
        long OrderId,
        long LineId,
        double QtyRemaining,
        ProductionLinePurpose Purpose,
        OrderType OrderType = OrderType.Internal,
        OrderStatus Status = OrderStatus.InProgress);
}
