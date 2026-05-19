using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderRedistributionTests
{
    [Fact]
    public void Redistribute_TransfersUnproducedQtyAndRefreshesCustomerReservation()
    {
        const long internalOrderId = 17;
        const long customerOrderId = 20;
        const long itemId = 1001;
        const long internalLineId = 1701;
        const long customerLineId = 2001;

        var internalOrder = new Order
        {
            Id = internalOrderId,
            OrderRef = "INT-17",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 1, 1)
        };
        var customerOrder = new Order
        {
            Id = customerOrderId,
            OrderRef = "CUST-20",
            Type = OrderType.Customer,
            PartnerId = 500,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 1, 2)
        };

        var internalLines = new List<OrderLine>
        {
            new() { Id = internalLineId, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = 400 }
        };
        var customerLines = new List<OrderLine>
        {
            new() { Id = customerLineId, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = 300 }
        };

        var orders = new Dictionary<long, Order>
        {
            [internalOrderId] = internalOrder,
            [customerOrderId] = customerOrder
        };

        double? internalQtyAfter = null;
        double? customerQtyAfter = null;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(It.IsAny<long>()))
            .Returns<long>(id => orders.TryGetValue(id, out var order) ? order : null);
        store.Setup(s => s.GetOrderLines(internalOrderId)).Returns(() => internalLines);
        store.Setup(s => s.GetOrderLines(customerOrderId)).Returns(() => customerLines);
        store.Setup(s => s.GetOrders()).Returns(() => orders.Values.ToArray());
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
                    QtyOrdered = internalLines[0].QtyOrdered,
                    QtyReceived = 100,
                    QtyRemaining = Math.Max(0, internalLines[0].QtyOrdered - 100)
                }
            ]);
        store.Setup(s => s.GetOrderReceiptRemaining(customerOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = customerLineId,
                    OrderId = customerOrderId,
                    ItemId = itemId,
                    QtyOrdered = customerLines[0].QtyOrdered,
                    QtyReceived = 0
                }
            ]);
        store.Setup(s => s.UpdateOrderLineQty(internalLineId, It.IsAny<double>()))
            .Callback<long, double>((_, qty) => internalQtyAfter = qty);
        store.Setup(s => s.UpdateOrderLineQty(customerLineId, It.IsAny<double>()))
            .Callback<long, double>((_, qty) => customerQtyAfter = qty);
        store.Setup(s => s.FindItemById(itemId))
            .Returns(new Item { Id = itemId, Name = "Item 1", ItemTypeId = 1, MaxQtyPerHu = 1000 });
        store.Setup(s => s.GetItemType(1))
            .Returns(new ItemType { Id = 1, Name = "Товар", EnableHuDistribution = false, EnableOrderReservation = true });
        store.Setup(s => s.GetLocations())
            .Returns([new Location { Id = 1, Code = "01", AutoHuDistributionEnabled = true }]);
        store.Setup(s => s.GetHuStockRows()).Returns(Array.Empty<HuStockRow>());
        store.Setup(s => s.GetHuOrderContextRows()).Returns(Array.Empty<HuOrderContextRow>());
        store.Setup(s => s.GetDocs()).Returns(Array.Empty<Doc>());
        var planByOrder = new Dictionary<long, List<OrderReceiptPlanLine>>
        {
            [internalOrderId] =
            [
                new OrderReceiptPlanLine
                {
                    Id = 1,
                    OrderId = internalOrderId,
                    OrderLineId = internalLineId,
                    ItemId = itemId,
                    QtyPlanned = 600,
                    ToHu = "HU-0000460",
                    SortOrder = 0
                },
                new OrderReceiptPlanLine
                {
                    Id = 2,
                    OrderId = internalOrderId,
                    OrderLineId = internalLineId,
                    ItemId = itemId,
                    QtyPlanned = 600,
                    ToHu = "HU-0000461",
                    SortOrder = 1
                }
            ],
            [customerOrderId] = []
        };
        store.Setup(s => s.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lines) =>
            {
                planByOrder[orderId] = lines?.ToList() ?? [];
            });
        store.Setup(s => s.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(orderId => planByOrder.TryGetValue(orderId, out var lines) ? lines : []);
        store.Setup(s => s.ReassignOpenProductionPalletsByHu(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<IReadOnlyList<string>>()));
        store.Setup(s => s.GetPartner(500)).Returns(new Partner { Id = 500, Name = "Customer", Code = "CUST" });
        OrderStatus? internalStatus = OrderStatus.InProgress;
        string? internalComment = null;
        store.Setup(s => s.UpdateOrderStatus(internalOrderId, It.IsAny<OrderStatus>()))
            .Callback<long, OrderStatus>((_, status) => internalStatus = status);
        store.Setup(s => s.UpdateOrder(It.IsAny<Order>()))
            .Callback<Order>(order =>
            {
                internalComment = order.Comment;
                internalStatus = order.Status;
            });

        var service = new OrderRedistributionService(store.Object);
        var result = service.Redistribute(internalOrderId, customerOrderId, itemId, 200);

        Assert.Equal(200, result.QtyTransferred, 3);
        Assert.Equal(200, result.QtyFromUnproduced, 3);
        Assert.Equal(0, result.QtyFromProducedStock, 3);
        Assert.Equal(200, internalQtyAfter);
        Assert.Equal(500, customerQtyAfter);
        Assert.Contains("HU-0000460", result.TransferredHuCodes, StringComparer.OrdinalIgnoreCase);
        var customerPlan = planByOrder[customerOrderId];
        Assert.Contains(customerPlan, line => string.Equals(line.ToHu, "HU-0000460", StringComparison.OrdinalIgnoreCase));
        var internalPlan = planByOrder[internalOrderId];
        Assert.Contains(internalPlan, line => string.Equals(line.ToHu, "HU-0000461", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(internalPlan, line => string.Equals(line.ToHu, "HU-0000460", StringComparison.OrdinalIgnoreCase));
        store.Verify(
            s => s.ReassignOpenProductionPalletsByHu(
                internalOrderId,
                customerOrderId,
                customerLineId,
                itemId,
                It.Is<IReadOnlyList<string>>(codes => codes.Contains("HU-0000460"))),
            Times.Once);
    }

    [Fact]
    public void Redistribute_WhenAllDemandTransferredWithoutProduction_MarksSourceMerged()
    {
        const long internalOrderId = 66;
        const long customerOrderId = 67;
        const long itemId = 100;
        const long internalLineId = 171;
        const long customerLineId = 172;

        var internalLines = new List<OrderLine>
        {
            new() { Id = internalLineId, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = 3600 }
        };
        var customerLines = new List<OrderLine>
        {
            new() { Id = customerLineId, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = 0 }
        };
        var orders = new Dictionary<long, Order>
        {
            [internalOrderId] = new()
            {
                Id = internalOrderId,
                OrderRef = "066",
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress,
                CreatedAt = DateTime.Now
            },
            [customerOrderId] = new()
            {
                Id = customerOrderId,
                OrderRef = "067",
                Type = OrderType.Customer,
                PartnerId = 1,
                Status = OrderStatus.InProgress,
                UseReservedStock = true,
                CreatedAt = DateTime.Now
            }
        };

        OrderStatus? internalStatus = OrderStatus.InProgress;
        string? internalComment = null;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(It.IsAny<long>())).Returns<long>(id => orders[id]);
        store.Setup(s => s.GetOrders()).Returns(() => orders.Values.ToArray());
        store.Setup(s => s.GetOrderLines(internalOrderId)).Returns(() => internalLines);
        store.Setup(s => s.GetOrderLines(customerOrderId)).Returns(() => customerLines);
        store.Setup(s => s.GetShippedTotalsByOrderLine(It.IsAny<long>())).Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetDocsByOrder(It.IsAny<long>())).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptRemaining(internalOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = internalLineId,
                    OrderId = internalOrderId,
                    ItemId = itemId,
                    QtyOrdered = 3600,
                    QtyReceived = 0,
                    QtyRemaining = 3600
                }
            ]);
        store.Setup(s => s.GetOrderReceiptRemaining(customerOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = customerLineId,
                    OrderId = customerOrderId,
                    ItemId = itemId,
                    QtyOrdered = 0,
                    QtyReceived = 0,
                    QtyRemaining = 0
                }
            ]);
        store.Setup(s => s.UpdateOrderLineQty(It.IsAny<long>(), It.IsAny<double>()))
            .Callback<long, double>((lineId, qty) =>
            {
                if (lineId == internalLineId)
                {
                    internalLines[0] = new OrderLine { Id = internalLineId, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = qty };
                }
                else
                {
                    customerLines[0] = new OrderLine { Id = customerLineId, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = qty };
                }
            });
        store.Setup(s => s.FindItemById(itemId))
            .Returns(new Item { Id = itemId, Name = "Item", ItemTypeId = 1, MaxQtyPerHu = 600 });
        store.Setup(s => s.GetItemType(1))
            .Returns(new ItemType { Id = 1, Name = "Товар", EnableOrderReservation = true });
        store.Setup(s => s.GetLocations())
            .Returns([new Location { Id = 1, Code = "MAIN", AutoHuDistributionEnabled = true }]);
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
        store.Setup(s => s.UpdateOrderStatus(internalOrderId, It.IsAny<OrderStatus>()))
            .Callback<long, OrderStatus>((_, status) => internalStatus = status);
        store.Setup(s => s.UpdateOrder(It.IsAny<Order>()))
            .Callback<Order>(order =>
            {
                internalComment = order.Comment;
                internalStatus = order.Status;
            });

        var service = new OrderRedistributionService(store.Object);
        var result = service.Redistribute(internalOrderId, customerOrderId, itemId, 3600);

        Assert.Equal(0, result.SourceQtyOrderedAfter);
        Assert.True(result.SourceMergeResult?.IsMerged);
        Assert.Equal(OrderStatus.Merged, internalStatus);
        Assert.Contains("Объединён с заказом №067", internalComment ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Redistribute_WhenProducedStockRequiredButMissing_Throws()
    {
        const long internalOrderId = 17;
        const long customerOrderId = 20;
        const long itemId = 1001;

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(internalOrderId))
            .Returns(new Order
            {
                Id = internalOrderId,
                OrderRef = "INT-17",
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress,
                CreatedAt = new DateTime(2026, 1, 1)
            });
        store.Setup(s => s.GetOrder(customerOrderId))
            .Returns(new Order
            {
                Id = customerOrderId,
                OrderRef = "CUST-20",
                Type = OrderType.Customer,
                PartnerId = 500,
                Status = OrderStatus.InProgress,
                UseReservedStock = true,
                CreatedAt = new DateTime(2026, 1, 2)
            });
        store.Setup(s => s.GetOrderLines(internalOrderId))
            .Returns([
                new OrderLine { Id = 1701, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = 400 }
            ]);
        store.Setup(s => s.GetOrderLines(customerOrderId))
            .Returns([
                new OrderLine { Id = 2001, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = 300 }
            ]);
        store.Setup(s => s.GetDocsByOrder(internalOrderId)).Returns(Array.Empty<Doc>());
        store.Setup(s => s.GetOrderReceiptRemaining(internalOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = 1701,
                    OrderId = internalOrderId,
                    ItemId = itemId,
                    QtyOrdered = 400,
                    QtyReceived = 100
                }
            ]);
        store.Setup(s => s.GetOrderReceiptPlanLines(internalOrderId)).Returns(Array.Empty<OrderReceiptPlanLine>());
        store.Setup(s => s.GetHuStockRows()).Returns(Array.Empty<HuStockRow>());
        store.Setup(s => s.GetHuOrderContextRows()).Returns(Array.Empty<HuOrderContextRow>());

        var service = new OrderRedistributionService(store.Object);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Redistribute(internalOrderId, customerOrderId, itemId, 350));

        Assert.Contains("Недостаточно выпущенного", ex.Message, StringComparison.Ordinal);
    }
}
