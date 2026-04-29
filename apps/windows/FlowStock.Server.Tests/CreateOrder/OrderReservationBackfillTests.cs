using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.CreateOrder;

public sealed class OrderReservationBackfillTests
{
    [Fact]
    public void DryRun_DoesNotChangeOrderReceiptPlanLines()
    {
        var scenario = BuildScenario();
        AddInternalProduction(scenario, itemId: 100, huCode: "HU-1", qty: 10);
        AddCustomerOrder(scenario, orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);

        var report = new OrderReservationBackfillService(scenario.Store.Object)
            .Run(new OrderReservationBackfillOptions(Apply: false));

        Assert.False(report.Applied);
        Assert.Equal(1, report.ChangedOrderCount);
        Assert.Empty(scenario.Plans[10]);
    }

    [Fact]
    public void Apply_RecreatesReservationPlanForHistoricalActiveOrder()
    {
        var scenario = BuildScenario();
        AddInternalProduction(scenario, itemId: 100, huCode: "HU-1", qty: 10);
        AddCustomerOrder(scenario, orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);

        var report = new OrderReservationBackfillService(scenario.Store.Object)
            .Run(new OrderReservationBackfillOptions(Apply: true));

        Assert.True(report.Applied);
        var line = Assert.Single(scenario.Plans[10]);
        Assert.Equal("HU-1", line.ToHu);
        Assert.Equal(10, line.QtyPlanned);
    }

    [Fact]
    public void Apply_DoesNotReserveShippedCustomerOrder()
    {
        var scenario = BuildScenario();
        AddInternalProduction(scenario, itemId: 100, huCode: "HU-1", qty: 10);
        AddCustomerOrder(
            scenario,
            orderId: 10,
            orderRef: "C-10",
            itemId: 100,
            qty: 10,
            useReservedStock: true,
            status: OrderStatus.Shipped,
            existingPlanHu: "HU-1");

        var report = new OrderReservationBackfillService(scenario.Store.Object)
            .Run(new OrderReservationBackfillOptions(Apply: true));

        Assert.Equal(0, report.ActiveCustomerOrderCount);
        Assert.Empty(scenario.Plans[10]);
    }

    [Fact]
    public void DryRun_ReportsDoubleReservationConflict()
    {
        var scenario = BuildScenario();
        AddInternalProduction(scenario, itemId: 100, huCode: "HU-1", qty: 10);
        AddCustomerOrder(scenario, orderId: 10, orderRef: "C-10", itemId: 100, qty: 5, useReservedStock: true, existingPlanHu: "HU-1");
        AddCustomerOrder(scenario, orderId: 11, orderRef: "C-11", itemId: 100, qty: 5, useReservedStock: true, existingPlanHu: "HU-1");

        var report = new OrderReservationBackfillService(scenario.Store.Object)
            .Run(new OrderReservationBackfillOptions(Apply: false));

        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal("HU-1", conflict.HuCode);
        Assert.Equal(100, conflict.ItemId);
        Assert.Equal(new List<long> { 10L, 11L }, conflict.Claims.Select(claim => claim.OrderId).ToList());
    }

    [Fact]
    public void Apply_DoesNotCreateLedgerMovement()
    {
        var scenario = BuildScenario();
        AddInternalProduction(scenario, itemId: 100, huCode: "HU-1", qty: 10);
        AddCustomerOrder(scenario, orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);
        var ledgerMovementCountBefore = scenario.LedgerMovementCount;

        new OrderReservationBackfillService(scenario.Store.Object)
            .Run(new OrderReservationBackfillOptions(Apply: true));

        Assert.Equal(ledgerMovementCountBefore, scenario.LedgerMovementCount);
        scenario.Store.Verify(store => store.AddLedgerEntry(It.IsAny<LedgerEntry>()), Times.Never);
    }

    [Fact]
    public void Apply_DoesNotReserveAlreadyShippedQuantityAgain()
    {
        var scenario = BuildScenario();
        AddInternalProduction(scenario, itemId: 100, huCode: "HU-1", qty: 10);
        AddCustomerOrder(scenario, orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);
        scenario.ShippedByOrderLine[1000] = 4;

        new OrderReservationBackfillService(scenario.Store.Object)
            .Run(new OrderReservationBackfillOptions(Apply: true));

        var line = Assert.Single(scenario.Plans[10]);
        Assert.Equal(6, line.QtyPlanned);
    }

    private static BackfillScenario BuildScenario()
    {
        var scenario = new BackfillScenario();
        scenario.Store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(scenario.Store.Object));
        scenario.Store.Setup(store => store.GetOrders())
            .Returns(() => scenario.Orders.Values.OrderBy(order => order.Id).ToArray());
        scenario.Store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.OrderLines.TryGetValue(orderId, out var lines)
                ? lines.ToArray()
                : Array.Empty<OrderLine>());
        scenario.Store.Setup(store => store.GetShippedTotalsByOrderLine(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.OrderLines.TryGetValue(orderId, out var lines)
                ? scenario.ShippedByOrderLine
                    .Where(pair => lines.Any(line => line.Id == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<long, double>());
        scenario.Store.Setup(store => store.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.OrderLines.TryGetValue(orderId, out var lines)
                ? lines.Select(line =>
                    {
                        var planned = scenario.Plans.TryGetValue(orderId, out var planLines)
                            ? planLines.Where(plan => plan.OrderLineId == line.Id).Sum(plan => plan.QtyPlanned)
                            : 0;
                        return new OrderReceiptLine
                        {
                            OrderLineId = line.Id,
                            OrderId = orderId,
                            ItemId = line.ItemId,
                            ItemName = "Item",
                            QtyOrdered = line.QtyOrdered,
                            QtyReceived = planned,
                            QtyRemaining = Math.Max(0, line.QtyOrdered - planned)
                        };
                    })
                    .ToArray()
                : Array.Empty<OrderReceiptLine>());
        scenario.Store.Setup(store => store.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.Plans.TryGetValue(orderId, out var lines)
                ? lines.Select(ClonePlanLine).ToArray()
                : Array.Empty<OrderReceiptPlanLine>());
        scenario.Store.Setup(store => store.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lines) =>
            {
                scenario.Plans[orderId] = lines.Select(ClonePlanLine).ToList();
            });
        scenario.Store.Setup(store => store.GetDocs()).Returns(() => scenario.Docs.ToArray());
        scenario.Store.Setup(store => store.GetDocLines(It.IsAny<long>()))
            .Returns<long>(docId => scenario.DocLines.TryGetValue(docId, out var lines)
                ? lines.ToArray()
                : Array.Empty<DocLine>());
        scenario.Store.Setup(store => store.GetHuStockRows()).Returns(() => scenario.HuStockRows.ToArray());
        scenario.Store.Setup(store => store.FindItemById(It.IsAny<long>()))
            .Returns<long>(itemId => scenario.Items.TryGetValue(itemId, out var item) ? item : null);
        scenario.Store.Setup(store => store.GetItemType(It.IsAny<long>()))
            .Returns<long>(typeId => scenario.ItemTypes.TryGetValue(typeId, out var type) ? type : null);
        scenario.Store.Setup(store => store.AddLedgerEntry(It.IsAny<LedgerEntry>()))
            .Callback(() => scenario.LedgerMovementCount++);
        return scenario;
    }

    private static void AddInternalProduction(BackfillScenario scenario, long itemId, string huCode, double qty)
    {
        scenario.Orders[1] = new Order
        {
            Id = 1,
            OrderRef = "INT-1",
            Type = OrderType.Internal,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 1, 1)
        };
        scenario.Docs.Add(new Doc
        {
            Id = 100,
            DocRef = "PRD-1",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 1,
            OrderRef = "INT-1",
            CreatedAt = new DateTime(2026, 1, 1),
            ClosedAt = new DateTime(2026, 1, 1, 1, 0, 0)
        });
        scenario.DocLines[100] =
        [
            new DocLine
            {
                Id = 101,
                DocId = 100,
                ItemId = itemId,
                Qty = qty,
                ToLocationId = 5,
                ToHu = huCode
            }
        ];
        scenario.HuStockRows.Add(new HuStockRow
        {
            HuCode = huCode,
            ItemId = itemId,
            LocationId = 5,
            Qty = qty
        });
    }

    private static void AddCustomerOrder(
        BackfillScenario scenario,
        long orderId,
        string orderRef,
        long itemId,
        double qty,
        bool useReservedStock,
        OrderStatus status = OrderStatus.InProgress,
        string? existingPlanHu = null)
    {
        scenario.ItemTypes[50] = new ItemType
        {
            Id = 50,
            Name = "Reserved type",
            EnableOrderReservation = true
        };
        scenario.Items[itemId] = new Item
        {
            Id = itemId,
            Name = "Item",
            ItemTypeId = 50
        };
        scenario.Orders[orderId] = new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = status,
            UseReservedStock = useReservedStock,
            CreatedAt = new DateTime(2026, 1, 2).AddMinutes(orderId)
        };
        scenario.OrderLines[orderId] =
        [
            new OrderLine
            {
                Id = orderId * 100,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = qty
            }
        ];
        scenario.Plans[orderId] = string.IsNullOrWhiteSpace(existingPlanHu)
            ? new List<OrderReceiptPlanLine>()
            :
            [
                new OrderReceiptPlanLine
                {
                    OrderId = orderId,
                    OrderLineId = orderId * 100,
                    ItemId = itemId,
                    QtyPlanned = qty,
                    ToLocationId = 5,
                    ToHu = existingPlanHu,
                    SortOrder = 0
                }
            ];
    }

    private static OrderReceiptPlanLine ClonePlanLine(OrderReceiptPlanLine line)
    {
        return new OrderReceiptPlanLine
        {
            Id = line.Id,
            OrderId = line.OrderId,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            QtyPlanned = line.QtyPlanned,
            ToLocationId = line.ToLocationId,
            ToLocationCode = line.ToLocationCode,
            ToLocationName = line.ToLocationName,
            ToHu = line.ToHu,
            SortOrder = line.SortOrder
        };
    }

    private sealed class BackfillScenario
    {
        public Mock<IDataStore> Store { get; } = new(MockBehavior.Strict);
        public Dictionary<long, Order> Orders { get; } = new();
        public Dictionary<long, List<OrderLine>> OrderLines { get; } = new();
        public Dictionary<long, List<OrderReceiptPlanLine>> Plans { get; } = new();
        public List<Doc> Docs { get; } = new();
        public Dictionary<long, IReadOnlyList<DocLine>> DocLines { get; } = new();
        public List<HuStockRow> HuStockRows { get; } = new();
        public Dictionary<long, Item> Items { get; } = new();
        public Dictionary<long, ItemType> ItemTypes { get; } = new();
        public Dictionary<long, double> ShippedByOrderLine { get; } = new();
        public int LedgerMovementCount { get; set; }
    }
}
