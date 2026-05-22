using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderHuReservationApplyServiceTests
{
    [Fact]
    public void ApplyLedgerStockHu_ReservesSelectedHu()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 4800, qtyShipped: 0);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-0000493", itemId: 6, qty: 600, shipReady: true));
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-0000494", itemId: 6, qty: 600, shipReady: true));

        var result = context.Apply(78, Line(203, "HU-0000493", "HU-0000494"));

        var applied = Assert.Single(result.AppliedLines);
        Assert.Equal(1200, applied.ReservedQty, 3);
        Assert.Equal(2, applied.SelectedHuCount);
        var planLines = context.GetPlanLines(78);
        Assert.Equal(2, planLines.Count);
        Assert.All(planLines, line => Assert.Equal(203, line.OrderLineId));
        Assert.Contains(planLines, line => line.ToHu == "HU-0000493" && line.QtyPlanned == 600);
        Assert.Contains(planLines, line => line.ToHu == "HU-0000494" && line.QtyPlanned == 600);
    }

    [Fact]
    public void ApplyInternalFilledHu_ReservesWithoutMutatingInternal()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600, qtyShipped: 0);
        context.SeedInternalOrderState(internalOrderId: 72, internalLineId: 501, itemId: 6, qtyOrdered: 600);
        context.SeedCandidate(Source(
            "INTERNAL_FILLED",
            "HU-FILLED",
            itemId: 6,
            qty: 600,
            shipReady: false,
            sourceOrderId: 72,
            sourceOrderRef: "072"));

        var result = context.Apply(78, Line(203, "HU-FILLED"));

        Assert.Equal(600, Assert.Single(result.AppliedLines).ReservedQty, 3);
        Assert.Equal(600, context.GetInternalQtyOrdered(72, 501), 3);
        Assert.False(context.InternalSourceMutated);
        var plan = Assert.Single(context.GetPlanLines(78));
        Assert.Equal("HU-FILLED", plan.ToHu);
        Assert.False(Assert.Single(result.AppliedLines).SelectedHu.Single().ShipReady);
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 1200, qtyShipped: 0);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-0000493", itemId: 6, qty: 600, shipReady: true));
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-0000494", itemId: 6, qty: 600, shipReady: true));
        var request = Line(203, "HU-0000493", "HU-0000494");

        context.Apply(78, request);
        context.Apply(78, request);

        Assert.Equal(2, context.GetPlanLines(78).Count);
    }

    [Fact]
    public void ApplyEmptySelection_RemovesReservationsForThatLine()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCustomerOrderLine(78, 204, itemId: 7, qtyOrdered: 300);
        context.SeedPlanLine(78, 203, itemId: 6, "HU-OLD", 100);
        context.SeedPlanLine(78, 204, itemId: 7, "HU-KEEP", 50);

        context.Apply(78, Line(203));

        var planLines = context.GetPlanLines(78);
        Assert.DoesNotContain(planLines, line => line.OrderLineId == 203);
        Assert.Contains(planLines, line => line.OrderLineId == 204 && line.ToHu == "HU-KEEP");
    }

    [Fact]
    public void ApplyDoesNotTouchOtherLines()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCustomerOrderLine(78, 204, itemId: 7, qtyOrdered: 300);
        context.SeedPlanLine(78, 204, itemId: 7, "HU-KEEP", 50);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-NEW", itemId: 6, qty: 100, shipReady: true));

        context.Apply(78, Line(203, "HU-NEW"));

        var planLines = context.GetPlanLines(78);
        Assert.Contains(planLines, line => line.OrderLineId == 204 && line.ToHu == "HU-KEEP");
        Assert.Contains(planLines, line => line.OrderLineId == 203 && line.ToHu == "HU-NEW");
    }

    [Fact]
    public void RejectHuReservedByOtherActiveCustomer()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCandidate(Source(
            "LEDGER_STOCK",
            "HU-BUSY",
            itemId: 6,
            qty: 100,
            shipReady: true,
            reservedByOrderId: 99,
            reservedByOrderRef: "SO-099"));

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(78, Line(203, "HU-BUSY")));
        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", ex.ErrorCode);
    }

    [Fact]
    public void RejectHuNotAvailableWhenCandidateMissingForLineItem()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-WRONG", itemId: 99, qty: 100, shipReady: true));

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(78, Line(203, "HU-WRONG")));
        Assert.Equal("HU_NOT_AVAILABLE", ex.ErrorCode);
    }

    [Fact]
    public void RejectHuReservedOnOtherLineOfSameOrder()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCustomerOrderLine(78, 204, itemId: 6, qtyOrdered: 300);
        context.SeedPlanLine(78, 204, itemId: 6, "HU-ON-204", 100);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-ON-204", itemId: 6, qty: 100, shipReady: true));

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(78, Line(203, "HU-ON-204")));
        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", ex.ErrorCode);
    }

    [Fact]
    public void RejectSelectedQtyExceedsRemainingLineQty()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 500, qtyShipped: 200);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-1", itemId: 6, qty: 400, shipReady: true));

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(78, Line(203, "HU-1")));
        Assert.Equal("SELECTED_QTY_EXCEEDS_LINE_REMAINING", ex.ErrorCode);
    }

    [Fact]
    public void RejectDuplicateHuInRequest()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCustomerOrderLine(78, 204, itemId: 6, qtyOrdered: 600);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-SHARED", itemId: 6, qty: 300, shipReady: true));

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(
            78,
            Line(203, "HU-SHARED"),
            Line(204, "HU-SHARED")));

        Assert.Equal("DUPLICATE_HU_IN_REQUEST", ex.ErrorCode);
    }

    [Fact]
    public void MixedHuItemSpecificApply()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600);
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-MIXED", itemId: 6, qty: 250, shipReady: true));
        context.SeedCandidate(Source("LEDGER_STOCK", "HU-MIXED", itemId: 7, qty: 350, shipReady: true));

        var result = context.Apply(78, Line(203, "HU-MIXED"));

        var applied = Assert.Single(result.AppliedLines);
        Assert.Equal(250, applied.ReservedQty, 3);
        Assert.Equal(250, Assert.Single(context.GetPlanLines(78)).QtyPlanned, 3);
    }

    [Fact]
    public void CustomerOrderOnly_RejectsInternalOrder()
    {
        var context = CreateContext();
        context.SeedInternalOrder(72, 501, itemId: 6, qtyOrdered: 600);

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(72, Line(501, "HU-1")));
        Assert.Equal("ORDER_NOT_CUSTOMER", ex.ErrorCode);
    }

    [Fact]
    public void RejectsClosedCustomerOrder()
    {
        var context = CreateContext();
        context.SeedCustomerOrder(78, 203, itemId: 6, qtyOrdered: 600, status: OrderStatus.Shipped);

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => context.Apply(78, Line(203, "HU-1")));
        Assert.Equal("ORDER_CLOSED", ex.ErrorCode);
    }

    private static HuReservationApplyTestContext CreateContext() => new();

    private static OrderHuReservationApplyLineRequest Line(long orderLineId, params string[] huCodes) =>
        new()
        {
            OrderLineId = orderLineId,
            SelectedHuCodes = huCodes
        };

    private static HuReservationCandidateSourceRow Source(
        string source,
        string huCode,
        long itemId,
        double qty,
        bool shipReady,
        long? sourceOrderId = null,
        string? sourceOrderRef = null,
        long? reservedByOrderId = null,
        string? reservedByOrderRef = null) =>
        new()
        {
            Source = source,
            HuCode = huCode,
            ItemId = itemId,
            Qty = qty,
            ShipReady = shipReady,
            SourceOrderId = sourceOrderId,
            SourceOrderRef = sourceOrderRef,
            ReservedByOrderId = reservedByOrderId,
            ReservedByOrderRef = reservedByOrderRef
        };

    private sealed class HuReservationApplyTestContext
    {
        private readonly Mock<IDataStore> _store = new();
        private readonly Dictionary<long, Order> _orders = new();
        private readonly Dictionary<long, List<OrderLine>> _orderLines = new();
        private readonly Dictionary<long, List<OrderShipmentLine>> _shipmentRemaining = new();
        private readonly Dictionary<long, List<OrderReceiptPlanLine>> _planLines = new();
        private readonly List<HuReservationCandidateSourceRow> _sources = new();
        private double _internalQtyOrdered = 600;
        private long _nextPlanLineId = 1;

        public bool InternalSourceMutated { get; private set; }

        public HuReservationApplyTestContext()
        {
            _store.As<IOptimizedHuReservationCandidatesStore>();
            _store.Setup(store => store.GetOrder(It.IsAny<long>()))
                .Returns<long>(id => _orders.TryGetValue(id, out var order) ? order : null);
            _store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
                .Returns<long>(id => _orderLines.TryGetValue(id, out var lines)
                    ? lines.Select(CloneOrderLine).ToArray()
                    : Array.Empty<OrderLine>());
            _store.Setup(store => store.GetOrderShipmentRemaining(It.IsAny<long>()))
                .Returns<long>(id => _shipmentRemaining.TryGetValue(id, out var lines)
                    ? lines.Select(CloneShipmentLine).ToArray()
                    : Array.Empty<OrderShipmentLine>());
            _store.Setup(store => store.GetOrderReceiptPlanLines(It.IsAny<long>()))
                .Returns<long>(id => GetPlanLines(id));
            _store.Setup(store => store.ReplaceOrderReceiptPlanLinesForOrderLines(
                    It.IsAny<long>(),
                    It.IsAny<IReadOnlyCollection<long>>(),
                    It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
                .Callback<long, IReadOnlyCollection<long>, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lineIds, replacement) =>
                {
                    if (!_planLines.TryGetValue(orderId, out var current))
                    {
                        current = [];
                        _planLines[orderId] = current;
                    }

                    var affected = lineIds.ToHashSet();
                    current.RemoveAll(line => affected.Contains(line.OrderLineId));
                    foreach (var line in replacement)
                    {
                        current.Add(new OrderReceiptPlanLine
                        {
                            Id = _nextPlanLineId++,
                            OrderId = orderId,
                            OrderLineId = line.OrderLineId,
                            ItemId = line.ItemId,
                            QtyPlanned = line.QtyPlanned,
                            ToHu = line.ToHu,
                            SortOrder = line.SortOrder
                        });
                    }
                });
            _store.As<IOptimizedHuReservationCandidatesStore>()
                .Setup(store => store.GetHuReservationCandidateSources(
                    It.IsAny<long?>(),
                    It.IsAny<IReadOnlyCollection<long>>(),
                    It.IsAny<IReadOnlyCollection<string>>()))
                .Returns<long?, IReadOnlyCollection<long>, IReadOnlyCollection<string>>((orderId, itemIds, excludeHuCodes) =>
                {
                    var exclude = new HashSet<string>(
                        excludeHuCodes ?? Array.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    return _sources
                        .Where(row => itemIds.Contains(row.ItemId))
                        .Where(row => !exclude.Contains(row.HuCode))
                        .ToArray();
                });
        }

        public IDataStore Store => _store.Object;

        public OrderHuReservationApplyResult Apply(long orderId, params OrderHuReservationApplyLineRequest[] lines)
        {
            var service = new OrderHuReservationApplyService(Store);
            return service.Apply(orderId, new OrderHuReservationApplyRequest { Lines = lines });
        }

        public IReadOnlyList<OrderReceiptPlanLine> GetPlanLines(long orderId)
        {
            return _planLines.TryGetValue(orderId, out var lines)
                ? lines.Select(ClonePlanLine).ToArray()
                : Array.Empty<OrderReceiptPlanLine>();
        }

        public double GetInternalQtyOrdered(long internalOrderId, long internalLineId) => _internalQtyOrdered;

        public void SeedCustomerOrder(
            long orderId,
            long lineId,
            long itemId,
            double qtyOrdered,
            double qtyShipped = 0,
            OrderStatus status = OrderStatus.InProgress)
        {
            _orders[orderId] = new Order
            {
                Id = orderId,
                OrderRef = $"SO-{orderId:000}",
                Type = OrderType.Customer,
                Status = status,
                PartnerId = 1
            };
            _orderLines[orderId] =
            [
                new OrderLine
                {
                    Id = lineId,
                    OrderId = orderId,
                    ItemId = itemId,
                    QtyOrdered = qtyOrdered
                }
            ];
            _shipmentRemaining[orderId] =
            [
                new OrderShipmentLine
                {
                    OrderLineId = lineId,
                    OrderId = orderId,
                    ItemId = itemId,
                    QtyOrdered = qtyOrdered,
                    QtyShipped = qtyShipped,
                    QtyRemaining = Math.Max(0, qtyOrdered - qtyShipped)
                }
            ];
        }

        public void SeedCustomerOrderLine(long orderId, long lineId, long itemId, double qtyOrdered, double qtyShipped = 0)
        {
            if (!_orders.ContainsKey(orderId))
            {
                SeedCustomerOrder(orderId, lineId, itemId, qtyOrdered, qtyShipped);
                return;
            }

            if (!_orderLines.TryGetValue(orderId, out var lines))
            {
                lines = [];
                _orderLines[orderId] = lines;
            }

            lines.Add(new OrderLine
            {
                Id = lineId,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = qtyOrdered
            });
            if (!_shipmentRemaining.TryGetValue(orderId, out var shipmentLines))
            {
                shipmentLines = [];
                _shipmentRemaining[orderId] = shipmentLines;
            }

            shipmentLines.Add(new OrderShipmentLine
            {
                OrderLineId = lineId,
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = qtyOrdered,
                QtyShipped = qtyShipped,
                QtyRemaining = Math.Max(0, qtyOrdered - qtyShipped)
            });
        }

        public void SeedInternalOrder(long orderId, long lineId, long itemId, double qtyOrdered)
        {
            _orders[orderId] = new Order
            {
                Id = orderId,
                OrderRef = $"INT-{orderId:000}",
                Type = OrderType.Internal,
                Status = OrderStatus.InProgress
            };
            _orderLines[orderId] =
            [
                new OrderLine
                {
                    Id = lineId,
                    OrderId = orderId,
                    ItemId = itemId,
                    QtyOrdered = qtyOrdered
                }
            ];
        }

        public void SeedInternalOrderState(long internalOrderId, long internalLineId, long itemId, double qtyOrdered)
        {
            _internalQtyOrdered = qtyOrdered;
            InternalSourceMutated = false;
            SeedInternalOrder(internalOrderId, internalLineId, itemId, qtyOrdered);
        }

        public void SeedPlanLine(long orderId, long orderLineId, long itemId, string huCode, double qty)
        {
            if (!_planLines.TryGetValue(orderId, out var lines))
            {
                lines = [];
                _planLines[orderId] = lines;
            }

            lines.Add(new OrderReceiptPlanLine
            {
                Id = _nextPlanLineId++,
                OrderId = orderId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                QtyPlanned = qty,
                ToHu = huCode
            });
        }

        public void SeedCandidate(HuReservationCandidateSourceRow source) => _sources.Add(source);

        private static OrderLine CloneOrderLine(OrderLine line) => new()
        {
            Id = line.Id,
            OrderId = line.OrderId,
            ItemId = line.ItemId,
            QtyOrdered = line.QtyOrdered
        };

        private static OrderShipmentLine CloneShipmentLine(OrderShipmentLine line) => new()
        {
            OrderLineId = line.OrderLineId,
            OrderId = line.OrderId,
            ItemId = line.ItemId,
            QtyOrdered = line.QtyOrdered,
            QtyShipped = line.QtyShipped,
            QtyRemaining = line.QtyRemaining
        };

        private static OrderReceiptPlanLine ClonePlanLine(OrderReceiptPlanLine line) => new()
        {
            Id = line.Id,
            OrderId = line.OrderId,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            QtyPlanned = line.QtyPlanned,
            ToHu = line.ToHu,
            SortOrder = line.SortOrder
        };
    }
}
