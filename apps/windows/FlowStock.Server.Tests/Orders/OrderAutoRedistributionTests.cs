using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderAutoRedistributionTests
{
    [Fact]
    public void ApplyFromOpenInternalOrders_TransfersMatchingItemWithoutIncreasingCustomerQty()
    {
        const long internalOrderId = 64;
        const long customerOrderId = 65;
        const long itemId = 6;
        const long internalLineId = 6401;
        const long customerLineId = 6501;

        var internalOrder = new Order
        {
            Id = internalOrderId,
            OrderRef = "INT-64",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 1, 1)
        };
        var customerOrder = new Order
        {
            Id = customerOrderId,
            OrderRef = "CUST-65",
            Type = OrderType.Customer,
            PartnerId = 500,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 1, 2)
        };

        double internalQty = 100;
        double customerQty = 100;
        var internalLines = new List<OrderLine>
        {
            new() { Id = internalLineId, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = internalQty }
        };
        var customerLines = new List<OrderLine>
        {
            new() { Id = customerLineId, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = customerQty }
        };

        var orders = new Dictionary<long, Order>
        {
            [internalOrderId] = internalOrder,
            [customerOrderId] = customerOrder
        };

        double? customerQtyAfterAll = null;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrders()).Returns(() => orders.Values.ToArray());
        store.Setup(s => s.GetOrder(customerOrderId)).Returns(customerOrder);
        store.Setup(s => s.GetOrder(internalOrderId)).Returns(() => orders[internalOrderId]);
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
                    QtyOrdered = internalLines[0].QtyOrdered,
                    QtyReceived = 0,
                    QtyRemaining = internalLines[0].QtyOrdered
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
        store.Setup(s => s.UpdateOrderLineQty(It.IsAny<long>(), It.IsAny<double>()))
            .Callback<long, double>((lineId, qty) =>
            {
                if (lineId == customerLineId)
                {
                    customerQty = qty;
                    customerQtyAfterAll = qty;
                    customerLines[0] = new OrderLine
                    {
                        Id = customerLineId,
                        OrderId = customerOrderId,
                        ItemId = itemId,
                        QtyOrdered = customerQty
                    };
                }

                if (lineId == internalLineId)
                {
                    internalQty = qty;
                    internalLines[0] = new OrderLine
                    {
                        Id = internalLineId,
                        OrderId = internalOrderId,
                        ItemId = itemId,
                        QtyOrdered = internalQty
                    };
                }
            });
        store.Setup(s => s.FindItemById(itemId))
            .Returns(new Item { Id = itemId, Name = "Item 6", ItemTypeId = 1, MaxQtyPerHu = 1000 });
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
                    QtyPlanned = 100,
                    ToHu = "HU-0000460",
                    SortOrder = 0
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
        store.Setup(s => s.UpdateOrderStatus(internalOrderId, It.IsAny<OrderStatus>()))
            .Callback<long, OrderStatus>((_, status) =>
            {
                orders[internalOrderId] = new Order
                {
                    Id = internalOrderId,
                    OrderRef = internalOrder.OrderRef,
                    Type = OrderType.Internal,
                    Status = status,
                    CreatedAt = internalOrder.CreatedAt
                };
            });

        var service = new OrderAutoRedistributionService(store.Object);
        var result = service.ApplyFromOpenInternalOrders(customerOrderId);

        Assert.True(result.HasTransfers);
        Assert.Single(result.Transfers);
        Assert.Equal(100, result.Transfers[0].QtyTransferred, 3);
        Assert.Equal(100, customerQtyAfterAll);
        Assert.Equal(0, internalQty);
    }

    [Fact]
    public void ApplyFromOpenInternalOrders_SkipsWhenCustomerReservationDisabled()
    {
        const long customerOrderId = 65;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(customerOrderId))
            .Returns(new Order
            {
                Id = customerOrderId,
                OrderRef = "CUST-65",
                Type = OrderType.Customer,
                PartnerId = 500,
                Status = OrderStatus.InProgress,
                UseReservedStock = false,
                CreatedAt = DateTime.Now
            });

        var service = new OrderAutoRedistributionService(store.Object);
        var result = service.ApplyFromOpenInternalOrders(customerOrderId);

        Assert.False(result.HasTransfers);
        Assert.Equal("CUSTOMER_RESERVATION_DISABLED", result.SkippedReason);
    }

    [Fact]
    public void ApplyFromOpenInternalOrders_WhenTargetHasNoOpenLines_ReturnsReasonCode()
    {
        const long customerOrderId = 65;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(customerOrderId))
            .Returns(new Order
            {
                Id = customerOrderId,
                OrderRef = "CUST-65",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress,
                UseReservedStock = true,
                CreatedAt = DateTime.Now
            });
        store.Setup(s => s.GetOrderLines(customerOrderId))
            .Returns([new OrderLine { Id = 1, OrderId = customerOrderId, ItemId = 6, QtyOrdered = 0 }]);

        var service = new OrderAutoRedistributionService(store.Object);
        var result = service.ApplyFromOpenInternalOrders(customerOrderId);

        Assert.False(result.HasTransfers);
        Assert.Equal("TARGET_CUSTOMER_HAS_NO_OPEN_LINES", result.SkippedReason);
    }

    [Fact]
    public void ApplyFromOpenInternalOrders_WhenMatchingInternalQtyZeroWithPalletPlan_ReturnsSpecificWarning()
    {
        const long internalOrderId = 66;
        const long customerOrderId = 67;
        const long itemId = 6;
        const long internalLineId = 6601;
        const long customerLineId = 6701;
        var internalOrder = new Order
        {
            Id = internalOrderId,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 0)
        };
        var customerOrder = new Order
        {
            Id = customerOrderId,
            OrderRef = "067",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 34)
        };
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetOrder(customerOrderId)).Returns(customerOrder);
        store.Setup(s => s.GetOrder(internalOrderId)).Returns(internalOrder);
        store.Setup(s => s.GetOrders()).Returns([internalOrder, customerOrder]);
        store.Setup(s => s.GetOrderLines(customerOrderId))
            .Returns([new OrderLine { Id = customerLineId, OrderId = customerOrderId, ItemId = itemId, QtyOrdered = 2400 }]);
        store.Setup(s => s.GetOrderLines(internalOrderId))
            .Returns([new OrderLine { Id = internalLineId, OrderId = internalOrderId, ItemId = itemId, QtyOrdered = 0 }]);
        store.Setup(s => s.GetOrderReceiptRemaining(internalOrderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = internalLineId,
                    OrderId = internalOrderId,
                    ItemId = itemId,
                    QtyOrdered = 0,
                    QtyReceived = 0,
                    QtyRemaining = 0
                }
            ]);
        store.Setup(s => s.GetDocsByOrder(internalOrderId))
            .Returns([
                new Doc
                {
                    Id = 162,
                    DocRef = "PRD-2026-000156",
                    Type = DocType.ProductionReceipt,
                    Status = DocStatus.Draft,
                    OrderId = internalOrderId,
                    OrderRef = "066",
                    CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
                }
            ]);
        store.Setup(s => s.GetProductionPalletsByDoc(162))
            .Returns([
                new ProductionPallet
                {
                    Id = 35,
                    PrdDocId = 162,
                    DocLineId = 1752,
                    OrderId = internalOrderId,
                    OrderLineId = internalLineId,
                    ItemId = itemId,
                    HuCode = "HU-0000462",
                    PlannedQty = 600,
                    Status = ProductionPalletStatus.Planned,
                    CreatedAt = DateTime.Now
                }
            ]);
        store.Setup(s => s.CountLedgerEntriesByDocId(162)).Returns(0);
        store.Setup(s => s.UpdateOrderStatus(internalOrderId, OrderStatus.InProgress));

        var service = new OrderAutoRedistributionService(store.Object);
        var result = service.ApplyFromOpenInternalOrders(customerOrderId);

        Assert.False(result.HasTransfers);
        Assert.Equal("SOURCE_INTERNAL_HAS_PALLET_PLAN_BUT_QTY_ZERO", result.SkippedReason);
        Assert.Contains(result.Warnings, warning => warning.Code == "SOURCE_INTERNAL_HAS_PALLET_PLAN_BUT_QTY_ZERO");
        Assert.Equal(1, result.OpenInternalCandidateCount);
        Assert.Equal(1, result.MatchingInternalCandidateCount);
    }
}
