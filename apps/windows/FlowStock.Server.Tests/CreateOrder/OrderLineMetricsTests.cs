using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.CreateOrder;

public sealed class OrderLineMetricsTests
{
    [Fact]
    public void CustomerOrderWithoutProtectedCoverage_DoesNotTreatFreeStockAsShipReady()
    {
        var line = BuildLine(itemId: 10, qtyOrdered: 30);
        var store = BuildMetricsStore(
            useReservedStock: false,
            line,
            physicalQty: 100,
            producedOrReservedQty: 0,
            itemTypeUsesOrderReservation: false);

        var result = new OrderService(store.Object).GetOrderLineViews(1).Single();

        Assert.Equal(100, result.QtyAvailable);
        Assert.Equal(0, result.CanShipNow);
        Assert.Equal(30, result.Shortage);
    }

    [Fact]
    public void CustomerOrderWithBoundLedgerHu_ForNonReservationType_CanShipFromProtectedCoverage()
    {
        var line = BuildLine(itemId: 10, qtyOrdered: 30);
        var store = BuildMetricsStore(
            useReservedStock: true,
            line,
            physicalQty: 100,
            producedOrReservedQty: 30,
            itemTypeUsesOrderReservation: false);

        var result = new OrderService(store.Object).GetOrderLineViews(1).Single();

        Assert.Equal(100, result.QtyAvailable);
        Assert.Equal(30, result.CanShipNow);
        Assert.Equal(0, result.Shortage);
    }

    [Fact]
    public void CustomerOrderWithReserve_ForReservationType_UsesReservedQtyForShipping()
    {
        var line = BuildLine(itemId: 10, qtyOrdered: 30);
        var store = BuildMetricsStore(
            useReservedStock: true,
            line,
            physicalQty: 100,
            producedOrReservedQty: 12,
            itemTypeUsesOrderReservation: true);

        var result = new OrderService(store.Object).GetOrderLineViews(1).Single();

        Assert.Equal(100, result.QtyAvailable);
        Assert.Equal(12, result.CanShipNow);
        Assert.Equal(18, result.Shortage);
    }

    [Fact]
    public void CustomerOrderWithoutAppliedReservation_ForReservationType_DoesNotTreatFreeStockAsShipReady()
    {
        var line = BuildLine(itemId: 10, qtyOrdered: 30);
        var store = BuildMetricsStore(
            useReservedStock: false,
            line,
            physicalQty: 100,
            producedOrReservedQty: 0,
            itemTypeUsesOrderReservation: true);

        var result = new OrderService(store.Object).GetOrderLineViews(1).Single();

        Assert.Equal(100, result.QtyAvailable);
        Assert.Equal(0, result.CanShipNow);
        Assert.Equal(30, result.Shortage);
    }

    private static OrderLineView BuildLine(long itemId, double qtyOrdered)
    {
        return new OrderLineView
        {
            Id = 101,
            OrderId = 1,
            ItemId = itemId,
            ItemName = "Товар",
            QtyOrdered = qtyOrdered
        };
    }

    private static Mock<IDataStore> BuildMetricsStore(
        bool useReservedStock,
        OrderLineView line,
        double physicalQty,
        double producedOrReservedQty,
        bool itemTypeUsesOrderReservation)
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(1)).Returns(new Order
        {
            Id = 1,
            OrderRef = "001",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = useReservedStock
        });
        store.Setup(s => s.GetOrderLineViews(1)).Returns([line]);
        store.Setup(s => s.GetOrderLines(1)).Returns([
            new OrderLine
            {
                Id = line.Id,
                OrderId = line.OrderId,
                ItemId = line.ItemId,
                QtyOrdered = line.QtyOrdered,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            }
        ]);
        store.Setup(s => s.GetLedgerTotalsByItem()).Returns(new Dictionary<long, double>
        {
            [line.ItemId] = physicalQty
        });
        store.Setup(s => s.GetShippedTotalsByOrderLine(1)).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetDocsByOrder(1)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptPlanLines(1)).Returns(producedOrReservedQty > 0
            ? [
                new OrderReceiptPlanLine
                {
                    Id = 1,
                    OrderId = line.OrderId,
                    OrderLineId = line.Id,
                    ItemId = line.ItemId,
                    QtyPlanned = producedOrReservedQty,
                    ToLocationId = 1,
                    ToHu = "HU-BOUND"
                }
            ]
            : Array.Empty<OrderReceiptPlanLine>());
        store.Setup(s => s.GetHuStockRows()).Returns(producedOrReservedQty > 0
            ? [
                new HuStockRow
                {
                    ItemId = line.ItemId,
                    LocationId = 1,
                    HuCode = "HU-BOUND",
                    Qty = producedOrReservedQty
                }
            ]
            : Array.Empty<HuStockRow>());
        store.Setup(s => s.GetOrderReceiptRemaining(1)).Returns([
            new OrderReceiptLine
            {
                OrderLineId = line.Id,
                OrderId = line.OrderId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QtyOrdered = line.QtyOrdered,
                QtyReceived = producedOrReservedQty,
                QtyRemaining = Math.Max(0, line.QtyOrdered - producedOrReservedQty)
            }
        ]);

        store.Setup(s => s.FindItemById(line.ItemId)).Returns(new Item
        {
            Id = line.ItemId,
            Name = line.ItemName,
            ItemTypeId = 5
        });
        store.Setup(s => s.GetItemType(5)).Returns(new ItemType
        {
            Id = 5,
            Name = "Товар",
            EnableOrderReservation = itemTypeUsesOrderReservation
        });

        return store;
    }
}
