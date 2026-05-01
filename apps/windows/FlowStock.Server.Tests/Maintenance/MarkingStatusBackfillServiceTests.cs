using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Maintenance;

public sealed class MarkingStatusBackfillServiceTests
{
    [Fact]
    public void DryRun_ReportsChangesWithoutMutations()
    {
        var (store, updates) = CreateStore(
            orders: new[]
            {
                CreateOrder(1, OrderStatus.InProgress, MarkingStatus.NotRequired)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = new[] { CreateOrderLine(1, 100) }
            });

        var report = new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: false));

        Assert.False(report.Applied);
        Assert.Equal(1, report.TotalScanned);
        Assert.Equal(1, report.ChangedToPrinted);
        Assert.Empty(updates);
    }

    [Fact]
    public void Apply_MarksLegacyActiveAndCompletedMarkableOrdersPrinted()
    {
        var timestamp = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var (store, updates) = CreateStore(
            orders: new[]
            {
                CreateOrder(1, OrderStatus.InProgress, MarkingStatus.NotRequired),
                CreateOrder(2, OrderStatus.Accepted, MarkingStatus.Required),
                CreateOrder(3, OrderStatus.Shipped, MarkingStatus.ExcelGenerated)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = new[] { CreateOrderLine(1, 100) },
                [2] = new[] { CreateOrderLine(2, 100) },
                [3] = new[] { CreateOrderLine(3, 100) }
            });

        var report = new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: true,
            Confirm: "APPLY",
            Timestamp: timestamp));

        Assert.True(report.Applied);
        Assert.Equal(3, report.ChangedToPrinted);
        Assert.Equal(new[] { 1L, 2L, 3L }, updates.Select(update => update.OrderId).ToArray());
        Assert.All(updates, update =>
        {
            Assert.Equal(MarkingStatus.Printed, update.Status);
            Assert.Equal(timestamp, update.Timestamp);
        });
    }

    [Fact]
    public void Apply_LeavesCancelledAndPendingOrdersNotPrinted()
    {
        var (store, updates) = CreateStore(
            orders: new[]
            {
                CreateOrder(1, OrderStatus.Cancelled, MarkingStatus.NotRequired),
                CreateOrder(2, OrderStatus.Draft, MarkingStatus.Required)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = new[] { CreateOrderLine(1, 100) },
                [2] = new[] { CreateOrderLine(2, 100) }
            });

        var report = new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: true,
            Confirm: "APPLY"));

        Assert.Equal(1, report.SkippedCancelled);
        Assert.Equal(1, report.SkippedPending);
        Assert.Empty(updates);
    }

    [Fact]
    public void Apply_SetsNotRequiredForLegacyOrdersWithoutMarkableLines()
    {
        var (store, updates) = CreateStore(
            orders: new[]
            {
                CreateOrder(1, OrderStatus.Accepted, MarkingStatus.Required)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = new[] { CreateOrderLine(1, 101) }
            });

        var report = new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: true,
            Confirm: "APPLY"));

        Assert.Equal(1, report.ChangedToNotRequired);
        var update = Assert.Single(updates);
        Assert.Equal(1, update.OrderId);
        Assert.Equal(MarkingStatus.NotRequired, update.Status);
    }

    [Fact]
    public void Apply_DoesNotDowngradeAlreadyPrintedOrders()
    {
        var (store, updates) = CreateStore(
            orders: new[]
            {
                CreateOrder(1, OrderStatus.InProgress, MarkingStatus.Printed)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = Array.Empty<OrderLine>()
            });

        var report = new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: true,
            Confirm: "APPLY"));

        Assert.Equal(1, report.AlreadyPrinted);
        Assert.Empty(updates);
    }

    [Fact]
    public void Apply_RestoresPrintedFromHistoricalLifecycleEvenWithoutCurrentMarkingNeed()
    {
        var timestamp = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var generatedAt = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var (store, updates) = CreateStore(
            orders: new[]
            {
                CreateOrder(
                    1,
                    OrderStatus.Shipped,
                    MarkingStatus.NotRequired,
                    markingExcelGeneratedAt: generatedAt)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = Array.Empty<OrderLine>()
            });

        var report = new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: true,
            Confirm: "APPLY",
            Timestamp: timestamp));

        Assert.Equal(1, report.ChangedToPrinted);
        var update = Assert.Single(updates);
        Assert.Equal(1, update.OrderId);
        Assert.Equal(MarkingStatus.Printed, update.Status);
        Assert.Equal(timestamp, update.Timestamp);
        store.Verify(s => s.GetOrderLines(1), Times.Never);
    }

    [Fact]
    public void Apply_DoesNotTouchLedgerDocsOrDocLines()
    {
        var (store, _) = CreateStore(
            orders: new[]
            {
                CreateOrder(1, OrderStatus.Shipped, MarkingStatus.NotRequired)
            },
            orderLines: new Dictionary<long, IReadOnlyList<OrderLine>>
            {
                [1] = new[] { CreateOrderLine(1, 100) }
            });

        new MarkingStatusBackfillService(store.Object).Run(new MarkingStatusBackfillOptions(
            new DateTime(2026, 4, 30),
            Apply: true,
            Confirm: "APPLY"));

        store.Verify(s => s.AddLedgerEntry(It.IsAny<LedgerEntry>()), Times.Never);
        store.Verify(s => s.UpdateDocStatus(It.IsAny<long>(), It.IsAny<DocStatus>(), It.IsAny<DateTime?>()), Times.Never);
        store.Verify(s => s.UpdateDocLineQty(
            It.IsAny<long>(),
            It.IsAny<double>(),
            It.IsAny<double?>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void Run_RequiresCutoff()
    {
        var (store, _) = CreateStore(Array.Empty<Order>(), new Dictionary<long, IReadOnlyList<OrderLine>>());

        Assert.Throws<ArgumentException>(() => new MarkingStatusBackfillService(store.Object).Run(
            new MarkingStatusBackfillOptions(CreatedBefore: null, Apply: false)));
    }

    [Fact]
    public void Apply_RequiresConfirmApply()
    {
        var (store, _) = CreateStore(Array.Empty<Order>(), new Dictionary<long, IReadOnlyList<OrderLine>>());

        Assert.Throws<InvalidOperationException>(() => new MarkingStatusBackfillService(store.Object).Run(
            new MarkingStatusBackfillOptions(new DateTime(2026, 4, 30), Apply: true, Confirm: null)));
    }

    private static (Mock<IDataStore> Store, List<(long OrderId, MarkingStatus Status, DateTime Timestamp)> Updates) CreateStore(
        IReadOnlyList<Order> orders,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLines)
    {
        var updates = new List<(long OrderId, MarkingStatus Status, DateTime Timestamp)>();
        var store = new Mock<IDataStore>(MockBehavior.Loose);
        store.Setup(s => s.GetItems(null))
            .Returns(new[]
            {
                new Item
                {
                    Id = 100,
                    Name = "Маркируемый",
                    Gtin = "04601234567890",
                    ItemTypeEnableMarking = true,
                    IsMarked = false
                },
                new Item
                {
                    Id = 101,
                    Name = "GTIN без типа ЧЗ",
                    Gtin = "04600000000000",
                    ItemTypeEnableMarking = false,
                    IsMarked = true
                }
            });
        store.Setup(s => s.GetOrders()).Returns(orders);
        store.Setup(s => s.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(orderId => orderLines.TryGetValue(orderId, out var lines)
                ? lines
                : Array.Empty<OrderLine>());
        store.Setup(s => s.UpdateOrderMarkingStatusForBackfill(
                It.IsAny<long>(),
                It.IsAny<MarkingStatus>(),
                It.IsAny<DateTime>()))
            .Callback<long, MarkingStatus, DateTime>((orderId, status, timestamp) => updates.Add((orderId, status, timestamp)));
        return (store, updates);
    }

    private static Order CreateOrder(
        long id,
        OrderStatus status,
        MarkingStatus markingStatus,
        DateTime? markingExcelGeneratedAt = null,
        DateTime? markingPrintedAt = null)
    {
        return new Order
        {
            Id = id,
            OrderRef = $"CO-{id}",
            Type = OrderType.Customer,
            Status = status,
            CreatedAt = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = markingStatus,
            MarkingExcelGeneratedAt = markingExcelGeneratedAt,
            MarkingPrintedAt = markingPrintedAt
        };
    }

    private static OrderLine CreateOrderLine(long orderId, long itemId)
    {
        return new OrderLine
        {
            Id = orderId * 10,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 1
        };
    }
}
