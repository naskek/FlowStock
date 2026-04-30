using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.CreateOrder;

public sealed class HuReservationPlanningTests
{
    [Fact]
    public void CreateCustomerOrder_WithReserve_DoesNotCreateLedgerAndDoesNotDoubleReserveHu()
    {
        var partner = new Partner { Id = 500, Name = "Customer A", Code = "CUST-A" };
        var itemId = 1001L;
        var internalOrderId = 10L;
        var existingCustomerOrderId = 20L;
        var huCode = "HU-000001";

        var orders = new Dictionary<long, Order>
        {
            [internalOrderId] = new()
            {
                Id = internalOrderId,
                OrderRef = "INT-10",
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress,
                CreatedAt = new DateTime(2026, 1, 1)
            },
            [existingCustomerOrderId] = new()
            {
                Id = existingCustomerOrderId,
                OrderRef = "CUST-20",
                Type = OrderType.Customer,
                PartnerId = partner.Id,
                Status = OrderStatus.InProgress,
                UseReservedStock = true,
                CreatedAt = new DateTime(2026, 1, 2)
            }
        };

        var orderLines = new Dictionary<long, List<OrderLine>>
        {
            [existingCustomerOrderId] =
            [
                new OrderLine { Id = 201, OrderId = existingCustomerOrderId, ItemId = itemId, QtyOrdered = 10 }
            ]
        };

        var receiptPlans = new Dictionary<long, List<OrderReceiptPlanLine>>
        {
            [existingCustomerOrderId] =
            [
                new OrderReceiptPlanLine
                {
                    Id = 1,
                    OrderId = existingCustomerOrderId,
                    OrderLineId = 201,
                    ItemId = itemId,
                    QtyPlanned = 10,
                    ToLocationId = 1,
                    ToHu = huCode,
                    SortOrder = 0
                }
            ]
        };

        var docs = new List<Doc>
        {
            new()
            {
                Id = 9001,
                DocRef = "PRD-9001",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Closed,
                OrderId = internalOrderId,
                OrderRef = "INT-10",
                CreatedAt = new DateTime(2026, 1, 1),
                ClosedAt = new DateTime(2026, 1, 1, 1, 0, 0)
            }
        };

        var docLinesByDoc = new Dictionary<long, IReadOnlyList<DocLine>>
        {
            [9001] =
            [
                new DocLine
                {
                    Id = 9101,
                    DocId = 9001,
                    ItemId = itemId,
                    Qty = 10,
                    ToLocationId = 1,
                    ToHu = huCode
                }
            ]
        };

        var huStockRows = new List<HuStockRow>
        {
            new() { HuCode = huCode, ItemId = itemId, LocationId = 1, Qty = 10 }
        };

        var nextOrderId = 100L;
        var nextOrderLineId = 1000L;
        var ledgerEntryCalls = 0;

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetPartner(partner.Id)).Returns(partner);
        store.Setup(s => s.FindItemById(itemId))
            .Returns(new Item { Id = itemId, Name = "Item 1", ItemTypeId = 1 });
        store.Setup(s => s.GetItemType(1))
            .Returns(new ItemType { Id = 1, Name = "Товар", EnableOrderReservation = true });
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.AddOrder(It.IsAny<Order>()))
            .Returns<Order>(order =>
            {
                var id = nextOrderId++;
                orders[id] = new Order
                {
                    Id = id,
                    OrderRef = order.OrderRef,
                    Type = order.Type,
                    PartnerId = order.PartnerId,
                    Status = order.Status,
                    CreatedAt = order.CreatedAt,
                    DueDate = order.DueDate,
                    Comment = order.Comment,
                    UseReservedStock = order.UseReservedStock
                };
                orderLines[id] = [];
                return id;
            });
        store.Setup(s => s.AddOrderLine(It.IsAny<OrderLine>()))
            .Returns<OrderLine>(line =>
            {
                var id = nextOrderLineId++;
                if (!orderLines.TryGetValue(line.OrderId, out var list))
                {
                    list = [];
                    orderLines[line.OrderId] = list;
                }

                list.Add(new OrderLine
                {
                    Id = id,
                    OrderId = line.OrderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                });
                return id;
            });
        store.Setup(s => s.GetOrders())
            .Returns(() => orders.Values.OrderBy(o => o.Id).ToArray());
        store.Setup(s => s.GetOrder(It.IsAny<long>()))
            .Returns<long>(id => orders.TryGetValue(id, out var order) ? order : null);
        store.Setup(s => s.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(id => orderLines.TryGetValue(id, out var lines)
                ? lines.OrderBy(line => line.Id).ToArray()
                : Array.Empty<OrderLine>());
        store.Setup(s => s.GetShippedTotalsByOrderLine(It.IsAny<long>()))
            .Returns(new Dictionary<long, double>());
        store.Setup(s => s.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns<long>(id => orderLines.TryGetValue(id, out var lines)
                ? lines.Select(line => new OrderReceiptLine
                    {
                        OrderLineId = line.Id,
                        OrderId = line.OrderId,
                        ItemId = line.ItemId,
                        QtyOrdered = line.QtyOrdered,
                        QtyReceived = 0
                    })
                    .ToArray()
                : Array.Empty<OrderReceiptLine>());
        store.Setup(s => s.GetDocs()).Returns(docs);
        store.Setup(s => s.GetDocsByOrder(It.IsAny<long>()))
            .Returns<long>(orderId => docs.Where(doc => doc.OrderId == orderId).ToArray());
        store.Setup(s => s.GetDocLines(It.IsAny<long>()))
            .Returns<long>(docId => docLinesByDoc.TryGetValue(docId, out var lines) ? lines : Array.Empty<DocLine>());
        store.Setup(s => s.GetHuStockRows()).Returns(huStockRows);
        store.Setup(s => s.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(orderId => receiptPlans.TryGetValue(orderId, out var lines)
                ? lines.Select(line => new OrderReceiptPlanLine
                    {
                        Id = line.Id,
                        OrderId = line.OrderId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        QtyPlanned = line.QtyPlanned,
                        ToLocationId = line.ToLocationId,
                        ToHu = line.ToHu,
                        SortOrder = line.SortOrder
                    })
                    .ToArray()
                : Array.Empty<OrderReceiptPlanLine>());
        store.Setup(s => s.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lines) =>
            {
                receiptPlans[orderId] = (lines ?? Array.Empty<OrderReceiptPlanLine>())
                    .Select((line, index) => new OrderReceiptPlanLine
                    {
                        Id = line.Id,
                        OrderId = orderId,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        QtyPlanned = line.QtyPlanned,
                        ToLocationId = line.ToLocationId,
                        ToHu = line.ToHu,
                        SortOrder = line.SortOrder == 0 ? index : line.SortOrder
                    })
                    .ToList();
            });
        store.Setup(s => s.AddLedgerEntry(It.IsAny<LedgerEntry>()))
            .Callback(() => ledgerEntryCalls++);

        var service = new OrderService(store.Object);
        var createdOrderId = service.CreateOrder(
            orderRef: "CUST-NEW",
            partnerId: partner.Id,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView { ItemId = itemId, ItemName = "Item 1", QtyOrdered = 10 }
            ],
            type: OrderType.Customer,
            bindReservedStockForCustomer: true);

        Assert.True(createdOrderId > 0);
        Assert.Equal(0, ledgerEntryCalls);
        Assert.True(receiptPlans.TryGetValue(createdOrderId, out var createdPlan));
        Assert.Empty(createdPlan!);
    }

    [Fact]
    public void GetOrderBoundHuByItem_InternalOrderOriginRemainsAfterCustomerReservation()
    {
        const long internalOrderId = 10;
        const long itemId = 1001;
        const string originHu = "HU-ORIGIN";
        const string reservedHu = "HU-RESERVED";

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetDocsByOrder(internalOrderId))
            .Returns([
                new Doc
                {
                    Id = 1,
                    Type = DocType.ProductionReceipt,
                    Status = DocStatus.Closed,
                    OrderId = internalOrderId,
                    OrderRef = "INT-10",
                    CreatedAt = new DateTime(2026, 1, 1)
                }
            ]);
        store.Setup(s => s.GetDocLines(1))
            .Returns([
                new DocLine
                {
                    Id = 101,
                    DocId = 1,
                    ItemId = itemId,
                    Qty = 5,
                    ToHu = originHu
                }
            ]);
        store.Setup(s => s.GetOrderReceiptPlanLines(internalOrderId))
            .Returns([
                new OrderReceiptPlanLine
                {
                    Id = 500,
                    OrderId = internalOrderId,
                    OrderLineId = 900,
                    ItemId = itemId,
                    QtyPlanned = 3,
                    ToHu = reservedHu
                }
            ]);

        var service = new OrderService(store.Object);
        var result = service.GetOrderBoundHuByItem(internalOrderId);

        Assert.True(result.TryGetValue(itemId, out var huSet));
        Assert.NotNull(huSet);
        Assert.Contains(huSet!, hu => string.Equals(hu, originHu, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(huSet!, hu => string.Equals(hu, reservedHu, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetOrderReceiptRemainingDetailed_ForCustomerOrder_DoesNotTurnReservationPlanIntoProductionNeed()
    {
        const long orderId = 20;
        const long orderLineId = 200;
        const long itemId = 1001;

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId))
            .Returns(new Order
            {
                Id = orderId,
                OrderRef = "CUST-20",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress,
                CreatedAt = new DateTime(2026, 1, 1),
                UseReservedStock = true
            });
        store.Setup(s => s.GetOrderReceiptPlanLines(orderId))
            .Returns([
                new OrderReceiptPlanLine
                {
                    Id = 500,
                    OrderId = orderId,
                    OrderLineId = orderLineId,
                    ItemId = itemId,
                    ItemName = "Item 1",
                    QtyPlanned = 20,
                    ToLocationId = 10,
                    ToLocationCode = "01",
                    ToHu = "HU-RESERVED",
                    SortOrder = 1
                }
            ]);
        store.Setup(s => s.GetOrderReceiptRemaining(orderId))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = orderLineId,
                    OrderId = orderId,
                    ItemId = itemId,
                    ItemName = "Item 1",
                    QtyOrdered = 20,
                    QtyReceived = 20,
                    QtyRemaining = 0
                }
            ]);

        var result = new OrderService(store.Object).GetOrderReceiptRemainingDetailed(orderId);

        Assert.Empty(result);
    }
}
