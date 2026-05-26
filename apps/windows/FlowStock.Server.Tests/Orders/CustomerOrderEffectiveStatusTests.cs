using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerOrderEffectiveStatusTests
{
    [Fact]
    public void CustomerOrderCoveredByWarehouseStockAndFilledPallets_IsAccepted()
    {
        const long orderId = 86;
        const long filledLineId = 861;
        const long warehouseLineId = 862;
        const long filledItemId = 1001;
        const long warehouseItemId = 1002;

        var order = new Order
        {
            Id = orderId,
            OrderRef = "086",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = false,
            CreatedAt = DateTime.UtcNow
        };

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId)).Returns(order);
        store.Setup(s => s.GetOrderLineViews(orderId)).Returns(
        [
            new OrderLineView
            {
                Id = filledLineId,
                OrderId = orderId,
                ItemId = filledItemId,
                ItemName = "Filled item",
                QtyOrdered = 5,
                PlannedPalletCount = 1,
                FilledPalletCount = 1,
                PlannedPalletQty = 5,
                FilledPalletQty = 5
            },
            new OrderLineView
            {
                Id = warehouseLineId,
                OrderId = orderId,
                ItemId = warehouseItemId,
                ItemName = "Warehouse item",
                QtyOrdered = 3
            }
        ]);
        store.Setup(s => s.GetLedgerTotalsByItem()).Returns(new Dictionary<long, double>
        {
            [filledItemId] = 5,
            [warehouseItemId] = 3
        });
        store.Setup(s => s.GetShippedTotalsByOrderLine(orderId)).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetOrderReceiptRemaining(orderId)).Returns(
        [
            new OrderReceiptLine
            {
                OrderId = orderId,
                OrderLineId = filledLineId,
                ItemId = filledItemId,
                QtyOrdered = 5,
                QtyReceived = 5,
                QtyRemaining = 0
            },
            new OrderReceiptLine
            {
                OrderId = orderId,
                OrderLineId = warehouseLineId,
                ItemId = warehouseItemId,
                QtyOrdered = 3,
                QtyReceived = 0,
                QtyRemaining = 3
            }
        ]);
        store.Setup(s => s.UpdateOrderStatus(orderId, OrderStatus.Accepted));

        var result = new OrderService(store.Object).GetOrder(orderId);

        Assert.NotNull(result);
        Assert.Equal(OrderStatus.Accepted, result.Status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Accepted), Times.Once);
    }
}
