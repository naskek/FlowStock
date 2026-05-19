using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class InternalOrderAutoStatusTests
{
    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.InProgress)]
    public void RefreshPersistedStatus_WhenQtyOrderedZeroAndNoProduction_DoesNotBecomeShipped(OrderStatus initialStatus)
    {
        const long orderId = 66;
        const long lineId = 6601;

        var store = CreateStore(orderId, initialStatus, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 0 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 0,
                QtyReceived = 0,
                QtyRemaining = 0
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.NotEqual(OrderStatus.Shipped, status);
        Assert.Equal(initialStatus == OrderStatus.Draft ? OrderStatus.Draft : OrderStatus.InProgress, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Never);
    }

    [Fact]
    public void RefreshPersistedStatus_WhenQtyOrderedPositiveAndNoProduction_StaysInProgress()
    {
        const long orderId = 67;
        const long lineId = 6701;

        var store = CreateStore(orderId, OrderStatus.InProgress, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 3600 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 3600,
                QtyReceived = 0,
                QtyRemaining = 3600
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.Equal(OrderStatus.InProgress, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Never);
    }

    [Fact]
    public void RefreshPersistedStatus_WhenStaleShippedStatusAndQtyZero_BecomesInProgress()
    {
        const long orderId = 66;
        const long lineId = 6601;

        var store = CreateStore(orderId, OrderStatus.Shipped, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 0 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 0,
                QtyReceived = 0,
                QtyRemaining = 0
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.Equal(OrderStatus.InProgress, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.InProgress), Times.Once);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Never);
    }

    [Fact]
    public void RefreshInternalOrderStatuses_CorrectsStaleShippedInternalOrder()
    {
        const long internalOrderId = 66;
        const long lineId = 6601;
        var persistedStatus = OrderStatus.Shipped;

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrders())
            .Returns(() =>
            [
                new Order
                {
                    Id = internalOrderId,
                    OrderRef = "INT-066",
                    Type = OrderType.Internal,
                    Status = persistedStatus,
                    CreatedAt = new DateTime(2026, 1, 1)
                }
            ]);
        store.Setup(s => s.GetOrder(internalOrderId))
            .Returns(() => new Order
            {
                Id = internalOrderId,
                OrderRef = "INT-066",
                Type = OrderType.Internal,
                Status = persistedStatus,
                CreatedAt = new DateTime(2026, 1, 1)
            });
        store.Setup(s => s.GetOrderLines(internalOrderId)).Returns([
            new OrderLine { Id = lineId, OrderId = internalOrderId, ItemId = 6, QtyOrdered = 0 }
        ]);
        store.Setup(s => s.GetOrderReceiptRemaining(internalOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = lineId,
                    OrderId = internalOrderId,
                    ItemId = 6,
                    QtyOrdered = 0,
                    QtyReceived = 0,
                    QtyRemaining = 0
                }
            ]);
        store.Setup(s => s.GetDocsByOrder(internalOrderId)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.UpdateOrderStatus(internalOrderId, OrderStatus.InProgress))
            .Callback<long, OrderStatus>((_, status) => persistedStatus = status);

        OrderService.RefreshInternalOrderStatuses(store.Object);

        Assert.Equal(OrderStatus.InProgress, persistedStatus);
        store.Verify(s => s.UpdateOrderStatus(internalOrderId, OrderStatus.InProgress), Times.Once);
    }

    [Fact]
    public void ApplyFromOpenInternalOrders_RefreshesStaleShippedInternalBeforeCandidateSelection()
    {
        const long internalOrderId = 66;
        const long customerOrderId = 65;
        const long itemId = 6;
        const long internalLineId = 6601;
        const long customerLineId = 6501;

        var internalStatus = OrderStatus.Shipped;
        var customerOrder = new Order
        {
            Id = customerOrderId,
            OrderRef = "CUST-065",
            Type = OrderType.Customer,
            PartnerId = 1,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 1, 2)
        };

        var internalLines = new List<OrderLine>
        {
            new() { Id = internalLineId, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = 100 }
        };
        var customerLines = new List<OrderLine>
        {
            new() { Id = customerLineId, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = 100 }
        };

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrders())
            .Returns(() =>
            [
                new Order
                {
                    Id = internalOrderId,
                    OrderRef = "INT-066",
                    Type = OrderType.Internal,
                    Status = internalStatus,
                    CreatedAt = new DateTime(2026, 1, 1)
                },
                customerOrder
            ]);
        store.Setup(s => s.GetOrder(customerOrderId)).Returns(customerOrder);
        store.Setup(s => s.GetOrder(internalOrderId))
            .Returns(() => new Order
            {
                Id = internalOrderId,
                OrderRef = "INT-066",
                Type = OrderType.Internal,
                Status = internalStatus,
                CreatedAt = new DateTime(2026, 1, 1)
            });
        store.Setup(s => s.GetOrderLines(customerOrderId)).Returns(() => customerLines);
        store.Setup(s => s.GetOrderLines(internalOrderId)).Returns(() => internalLines);
        store.Setup(s => s.GetShippedTotalsByOrderLine(It.IsAny<long>())).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetDocsByOrder(internalOrderId)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetDocsByOrder(customerOrderId)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptRemaining(internalOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = internalLineId,
                    OrderId = internalOrderId,
                    ItemId = itemId,
                    QtyOrdered = 100,
                    QtyReceived = 0,
                    QtyRemaining = 100
                }
            ]);
        store.Setup(s => s.GetOrderReceiptRemaining(customerOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = customerLineId,
                    OrderId = customerOrderId,
                    ItemId = itemId,
                    QtyOrdered = 100,
                    QtyReceived = 0
                }
            ]);
        store.Setup(s => s.UpdateOrderStatus(internalOrderId, It.IsAny<OrderStatus>()))
            .Callback<long, OrderStatus>((_, status) => internalStatus = status);
        store.Setup(s => s.UpdateOrder(It.IsAny<Order>()));
        store.Setup(s => s.HasProductionPallets(It.IsAny<long>())).Returns(false);
        store.Setup(s => s.CountLedgerEntriesByDocId(It.IsAny<long>())).Returns(0);
        store.Setup(s => s.UpdateOrderLineQty(It.IsAny<long>(), It.IsAny<double>()))
            .Callback<long, double>((lineId, qty) =>
            {
                if (lineId == customerLineId)
                {
                    customerLines[0] = new OrderLine
                    {
                        Id = customerLineId,
                        OrderId = customerOrderId,
                        ItemId = itemId,
                        QtyOrdered = qty
                    };
                }

                if (lineId == internalLineId)
                {
                    internalLines[0] = new OrderLine
                    {
                        Id = internalLineId,
                        OrderId = internalOrderId,
                        ItemId = itemId,
                        QtyOrdered = qty
                    };
                }
            });
        store.Setup(s => s.FindItemById(itemId))
            .Returns(new Item { Id = itemId, Name = "Item", ItemTypeId = 1, MaxQtyPerHu = 1000 });
        store.Setup(s => s.GetItemType(1))
            .Returns(new ItemType { Id = 1, Name = "Товар", EnableHuDistribution = false, EnableOrderReservation = true });
        store.Setup(s => s.GetLocations())
            .Returns([new Location { Id = 1, Code = "01", AutoHuDistributionEnabled = true }]);
        store.Setup(s => s.GetHuStockRows()).Returns(Array.Empty<HuStockRow>());
        store.Setup(s => s.GetHuOrderContextRows()).Returns(Array.Empty<HuOrderContextRow>());
        store.Setup(s => s.GetDocs()).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptPlanLines(It.IsAny<long>())).Returns(Array.Empty<OrderReceiptPlanLine>());
        store.Setup(s => s.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()));
        store.Setup(s => s.ReassignOpenProductionPalletsByHu(
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<IReadOnlyList<string>>()));
        store.Setup(s => s.GetPartner(1)).Returns(new Partner { Id = 1, Name = "Customer", Code = "CUST" });

        var service = new OrderAutoRedistributionService(store.Object);
        var result = service.ApplyFromOpenInternalOrders(customerOrderId);

        store.Verify(s => s.UpdateOrderStatus(internalOrderId, OrderStatus.InProgress), Times.Once);
        store.Verify(s => s.UpdateOrderStatus(internalOrderId, OrderStatus.Merged), Times.Once);
        Assert.True(result.HasTransfers);
        Assert.Equal(OrderStatus.Merged, internalStatus);
    }

    [Fact]
    public void RefreshPersistedStatus_WhenFullyCoveredByClosedProduction_BecomesShipped()
    {
        const long orderId = 68;
        const long lineId = 6801;

        var store = CreateStore(orderId, OrderStatus.InProgress, [
            new OrderLine { Id = lineId, OrderId = orderId, ItemId = 6, QtyOrdered = 3600 }
        ], [
            new OrderReceiptLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = 6,
                QtyOrdered = 3600,
                QtyReceived = 3600,
                QtyRemaining = 0
            }
        ]);
        store.Setup(s => s.GetDocsByOrder(orderId)).Returns(Array.Empty<Doc>());

        var service = new OrderService(store.Object);
        var status = service.RefreshPersistedStatus(orderId);

        Assert.Equal(OrderStatus.Shipped, status);
        store.Verify(s => s.UpdateOrderStatus(orderId, OrderStatus.Shipped), Times.Once);
    }

    private static Mock<IDataStore> CreateStore(
        long orderId,
        OrderStatus status,
        IReadOnlyList<OrderLine> lines,
        IReadOnlyList<OrderReceiptLine> receiptRemaining)
    {
        var order = new Order
        {
            Id = orderId,
            OrderRef = $"INT-{orderId}",
            Type = OrderType.Internal,
            Status = status,
            CreatedAt = new DateTime(2026, 1, 1)
        };

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId)).Returns(order);
        store.Setup(s => s.GetOrderLines(orderId)).Returns(lines);
        store.Setup(s => s.GetOrderReceiptRemaining(orderId)).Returns(receiptRemaining);
        store.Setup(s => s.UpdateOrderStatus(orderId, It.IsAny<OrderStatus>()));
        return store;
    }
}
