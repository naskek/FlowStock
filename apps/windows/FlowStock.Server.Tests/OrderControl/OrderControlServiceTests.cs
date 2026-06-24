using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;
using System.Threading;

namespace FlowStock.Server.Tests.OrderControl;

public sealed class OrderControlServiceTests
{
    [Fact]
    public void CreateControl_DoesNotTouchLedgerDocsOrOrderStatus()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);

        var result = service.Create([1], "tester", null);

        Assert.True(result.Success);
        Assert.NotNull(result.Task);
        Assert.Equal(1, result.Task.Task.ExpectedHuCount);
        harness.Store.Verify(store => store.AddLedgerEntry(It.IsAny<LedgerEntry>()), Times.Never);
        harness.Store.Verify(store => store.AddDoc(It.IsAny<Doc>()), Times.Never);
        harness.Store.Verify(store => store.AddDocLine(It.IsAny<DocLine>()), Times.Never);
        harness.Store.Verify(store => store.UpdateOrderStatus(It.IsAny<long>(), It.IsAny<OrderStatus>()), Times.Never);
    }

    [Fact]
    public void CreateControl_RejectsOutboundInProgress()
    {
        var harness = new Harness();
        harness.Store.Setup(store => store.HasStartedOutboundForOrder(1)).Returns(true);
        var service = new OrderControlService(harness.Store.Object);

        var result = service.Create([1], "tester", null);

        Assert.False(result.Success);
        Assert.Equal(OrderControlErrorCodes.OutboundInProgress, result.ErrorCode);
    }

    [Fact]
    public void MixedHu_CountsAsOnePhysicalHu()
    {
        var harness = new Harness();
        harness.PlanLines =
        [
            new OrderReceiptPlanLine { Id = 1, OrderId = 1, OrderLineId = 10, ItemId = 100, ItemName = "Товар A", QtyPlanned = 10, ToHu = "MIX-1" },
            new OrderReceiptPlanLine { Id = 2, OrderId = 1, OrderLineId = 11, ItemId = 101, ItemName = "Товар B", QtyPlanned = 5, ToHu = "MIX-1" }
        ];
        harness.StockRows =
        [
            new HuStockRow { HuCode = "MIX-1", ItemId = 100, LocationId = 1, Qty = 10 },
            new HuStockRow { HuCode = "MIX-1", ItemId = 101, LocationId = 1, Qty = 5 }
        ];
        harness.ShipmentRemaining =
        [
            new OrderShipmentLine { OrderLineId = 10, ItemId = 100, ItemName = "Товар A", QtyOrdered = 10, QtyShipped = 0, QtyRemaining = 10 },
            new OrderShipmentLine { OrderLineId = 11, ItemId = 101, ItemName = "Товар B", QtyOrdered = 5, QtyShipped = 0, QtyRemaining = 5 }
        ];
        harness.RefreshReadModelSetups();
        var service = new OrderControlService(harness.Store.Object);

        var result = service.Create([1], "tester", null);

        Assert.True(result.Success);
        Assert.Equal(1, result.Task!.Task.ExpectedHuCount);
        Assert.Equal(2, result.Task.HuLines.Count);
    }

    [Fact]
    public void DuplicateScan_IsIdempotent()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var created = service.Create([1], "tester", null);
        Assert.True(created.Success);
        var taskId = created.Task!.Task.Id;

        var first = service.Scan(taskId, "HU-1", "REQ-1", "TSD-1", null);
        var second = service.Scan(taskId, "HU-1", "REQ-2", "TSD-2", null);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(second.AlreadyChecked);
        Assert.Equal(1, second.Task!.Task.CheckedHuCount);
    }

    [Fact]
    public void Scan_IgnoresOwnActiveControlConflict()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var created = service.Create([1], "tester", null);
        var taskId = created.Task!.Task.Id;

        var active = harness.Store.Object.FindActiveOrderControlForOrder(1);
        Assert.NotNull(active);
        Assert.Equal(taskId, active.Task.Id);

        var scan = service.Scan(taskId, "HU-1", "REQ-OWN-ACTIVE", "TSD-1", null);

        Assert.True(scan.Success);
        Assert.Equal(OrderControlHuStatus.Checked, harness.GetHu("HU-1")!.Status);
    }

    [Fact]
    public void RetryAcceptedScan_ReturnsOriginalSuccess()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;

        var first = service.Scan(taskId, "HU-1", "REQ-ACCEPT", "TSD-1", null);
        var retry = service.Scan(taskId, "HU-1", "REQ-ACCEPT", "TSD-2", null);

        Assert.True(first.Success);
        Assert.True(retry.Success);
        Assert.False(retry.AlreadyChecked);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.ScanAccepted));
    }

    [Fact]
    public void RetryRejectedForeignHu_ReturnsOriginalFailure()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;

        var first = service.Scan(taskId, "HU-FOREIGN", "REQ-FOREIGN", "TSD-1", null);
        var retry = service.Scan(taskId, "HU-FOREIGN", "REQ-FOREIGN", "TSD-2", null);

        Assert.False(first.Success);
        Assert.False(retry.Success);
        Assert.Equal(OrderControlErrorCodes.HuNotInTask, retry.ErrorCode);
        Assert.Equal(first.Message, retry.Message);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.ScanRejected));
    }

    [Fact]
    public void RetryDiscrepancy_ReturnsOriginalFailure()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        harness.StockRows = [];
        harness.RefreshReadModelSetups();

        var first = service.Scan(taskId, "HU-1", "REQ-DISCREPANCY", "TSD-1", null);
        var retry = service.Scan(taskId, "HU-1", "REQ-DISCREPANCY", "TSD-2", null);

        Assert.False(first.Success);
        Assert.False(retry.Success);
        Assert.Equal(OrderControlErrorCodes.HuNoPhysicalStock, retry.ErrorCode);
        Assert.Equal(first.Message, retry.Message);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.Discrepancy));
    }

    [Fact]
    public void RetrySameRequestWithDifferentHu_ReturnsIdempotencyConflict()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;

        var first = service.Scan(taskId, "HU-1", "REQ-SAME", "TSD-1", null);
        var retry = service.Scan(taskId, "HU-OTHER", "REQ-SAME", "TSD-2", null);

        Assert.True(first.Success);
        Assert.False(retry.Success);
        Assert.Equal(OrderControlErrorCodes.IdempotencyConflict, retry.ErrorCode);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.ScanAccepted));
    }

    [Fact]
    public void Complete_RejectsIncompleteAndDiscrepancy()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var created = service.Create([1], "tester", null);
        var taskId = created.Task!.Task.Id;

        var incomplete = service.Complete(taskId, "TSD-1", null);
        Assert.False(incomplete.Success);
        Assert.Equal(OrderControlErrorCodes.TaskIncomplete, incomplete.ErrorCode);

        harness.StockRows = [];
        harness.RefreshReadModelSetups();
        var scan = service.Scan(taskId, "HU-1", "REQ-DISC", "TSD-1", null);
        Assert.False(scan.Success);

        var complete = service.Complete(taskId, "TSD-1", null);
        Assert.False(complete.Success);
        Assert.Equal(OrderControlErrorCodes.ExpectedSetChanged, complete.ErrorCode);
    }

    [Fact]
    public void Complete_RejectsWhenExpectedSetGainsHuAfterAllOriginalHusChecked()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        Assert.True(service.Scan(taskId, "HU-1", "REQ-HU1", "TSD-1", null).Success);

        harness.PlanLines =
        [
            new OrderReceiptPlanLine { Id = 1, OrderId = 1, OrderLineId = 10, ItemId = 100, ItemName = "Товар", QtyPlanned = 10, ToHu = "HU-1" },
            new OrderReceiptPlanLine { Id = 2, OrderId = 1, OrderLineId = 11, ItemId = 101, ItemName = "Товар B", QtyPlanned = 5, ToHu = "HU-2" }
        ];
        harness.StockRows =
        [
            new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 10 },
            new HuStockRow { HuCode = "HU-2", ItemId = 101, LocationId = 1, Qty = 5 }
        ];
        harness.ShipmentRemaining =
        [
            new OrderShipmentLine { OrderLineId = 10, ItemId = 100, ItemName = "Товар", QtyOrdered = 10, QtyShipped = 0, QtyRemaining = 10 },
            new OrderShipmentLine { OrderLineId = 11, ItemId = 101, ItemName = "Товар B", QtyOrdered = 5, QtyShipped = 0, QtyRemaining = 5 }
        ];
        harness.RefreshReadModelSetups();

        var complete = service.Complete(taskId, "TSD-1", null);

        Assert.False(complete.Success);
        Assert.Equal(OrderControlErrorCodes.ExpectedSetChanged, complete.ErrorCode);
        Assert.NotEqual(OrderControlTaskStatus.Completed, harness.GetTask(taskId)!.Status);
    }

    [Fact]
    public void Complete_RejectsWhenMixedHuCompositionChangesAfterLastScan()
    {
        var harness = new Harness();
        harness.UseMixedHu(qtyB: 5);
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        Assert.True(service.Scan(taskId, "MIX-1", "REQ-MIX", "TSD-1", null).Success);

        harness.UseMixedHu(qtyB: 6);

        var complete = service.Complete(taskId, "TSD-1", null);

        Assert.False(complete.Success);
        Assert.Equal(OrderControlErrorCodes.ExpectedSetChanged, complete.ErrorCode);
        Assert.NotEqual(OrderControlTaskStatus.Completed, harness.GetTask(taskId)!.Status);
    }

    [Fact]
    public void Complete_IsIdempotentAfterCompletedWithoutNewEvent()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        Assert.True(service.Scan(taskId, "HU-1", "REQ-COMPLETE", "TSD-1", null).Success);

        var first = service.Complete(taskId, "TSD-1", null);
        var eventsAfterFirst = harness.EventCount(OrderControlEventType.Completed);
        var second = service.Complete(taskId, "TSD-2", null);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(OrderControlTaskStatus.Completed, second.Task!.Task.Status);
        Assert.Equal(eventsAfterFirst, harness.EventCount(OrderControlEventType.Completed));
    }

    [Fact]
    public void Cancel_DoesNotCancelCompleted_AndRepeatedCancelIsIdempotent()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        Assert.True(service.Scan(taskId, "HU-1", "REQ-COMPLETE-CANCEL", "TSD-1", null).Success);
        Assert.True(service.Complete(taskId, "TSD-1", null).Success);
        var completedEvents = harness.EventCount(OrderControlEventType.Completed);

        var afterCancelAttempt = service.Cancel(taskId, "tester");

        Assert.Equal(OrderControlTaskStatus.Completed, afterCancelAttempt!.Task.Status);
        Assert.Equal(0, harness.EventCount(OrderControlEventType.Cancelled));
        Assert.Equal(completedEvents, harness.EventCount(OrderControlEventType.Completed));

        var cancelledTaskId = service.Create([1], "tester", null).Task!.Task.Id;
        service.Cancel(cancelledTaskId, "tester");
        var cancelEvents = harness.EventCount(OrderControlEventType.Cancelled);
        var repeated = service.Cancel(cancelledTaskId, "tester");

        Assert.Equal(OrderControlTaskStatus.Cancelled, repeated!.Task.Status);
        Assert.Equal(cancelEvents, harness.EventCount(OrderControlEventType.Cancelled));
    }

    [Fact]
    public void CancelledTask_CannotScanOrComplete()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        service.Cancel(taskId, "tester");

        var scan = service.Scan(taskId, "HU-1", "REQ-CANCELLED-SCAN", "TSD-1", null);
        var complete = service.Complete(taskId, "TSD-1", null);

        Assert.False(scan.Success);
        Assert.Equal(OrderControlErrorCodes.TaskCancelled, scan.ErrorCode);
        Assert.False(complete.Success);
        Assert.Equal(OrderControlErrorCodes.TaskCancelled, complete.ErrorCode);
    }

    [Fact]
    public void OutboundService_BlocksScanAndCompleteWhenControlActive()
    {
        var harness = new Harness();
        harness.Store.Setup(store => store.HasActiveOrderControlForOrder(1)).Returns(true);
        var outbound = new OutboundPickingService(harness.Store.Object, new DocumentService(harness.Store.Object));

        var scan = outbound.Scan(1, "HU-1", "TSD-1");
        var complete = outbound.Complete(1);

        Assert.False(scan.Success);
        Assert.Equal("ORDER_CONTROL_ACTIVE", scan.ErrorCode);
        Assert.False(complete.Success);
        Assert.Equal("ORDER_CONTROL_ACTIVE", complete.ErrorCode);
    }

    [Fact]
    public void ConcurrentSameRequestId_IsSerializedAndCreatesOneEvent()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        harness.EnableTaskLockBarrier();

        var (first, second) = RunConcurrently(
            () => service.Scan(taskId, "HU-1", "REQ-CONCURRENT", "TSD-1", null),
            () => service.Scan(taskId, "HU-1", "REQ-CONCURRENT", "TSD-2", null));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.ScanAccepted));
        Assert.Equal(1, harness.RequestEventCount("REQ-CONCURRENT"));
        Assert.Equal(1, harness.GetTask(taskId)!.CheckedHuCount);
    }

    [Fact]
    public void ConcurrentComplete_IsSerializedAndCreatesOneCompletedEvent()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        Assert.True(service.Scan(taskId, "HU-1", "REQ-BEFORE-COMPLETE", "TSD-1", null).Success);
        harness.EnableTaskLockBarrier();

        var (first, second) = RunConcurrently(
            () => service.Complete(taskId, "TSD-1", null),
            () => service.Complete(taskId, "TSD-2", null));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(OrderControlTaskStatus.Completed, harness.GetTask(taskId)!.Status);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.Completed));
    }

    [Fact]
    public void ConcurrentCompleteAndCancel_RespectsTaskLockWinner()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        Assert.True(service.Scan(taskId, "HU-1", "REQ-BEFORE-RACE", "TSD-1", null).Success);
        harness.EnableTaskLockBarrier();

        var (complete, cancel) = RunConcurrently(
            () => service.Complete(taskId, "TSD-1", null),
            () => service.Cancel(taskId, "tester"));

        var finalStatus = harness.GetTask(taskId)!.Status;
        if (string.Equals(finalStatus, OrderControlTaskStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(complete.Success);
            Assert.Equal(OrderControlTaskStatus.Completed, cancel!.Task.Status);
            Assert.Equal(1, harness.EventCount(OrderControlEventType.Completed));
            Assert.Equal(0, harness.EventCount(OrderControlEventType.Cancelled));
        }
        else
        {
            Assert.Equal(OrderControlTaskStatus.Cancelled, finalStatus);
            Assert.False(complete.Success);
            Assert.Equal(OrderControlErrorCodes.TaskCancelled, complete.ErrorCode);
            Assert.Equal(OrderControlTaskStatus.Cancelled, cancel!.Task.Status);
            Assert.Equal(0, harness.EventCount(OrderControlEventType.Completed));
            Assert.Equal(1, harness.EventCount(OrderControlEventType.Cancelled));
        }
    }

    [Fact]
    public void ConcurrentScanAndCancel_ProducesSerializedTerminalOutcome()
    {
        var harness = new Harness();
        var service = new OrderControlService(harness.Store.Object);
        var taskId = service.Create([1], "tester", null).Task!.Task.Id;
        harness.EnableTaskLockBarrier();

        var (scan, cancel) = RunConcurrently(
            () => service.Scan(taskId, "HU-1", "REQ-SCAN-CANCEL", "TSD-1", null),
            () => service.Cancel(taskId, "tester"));

        Assert.Equal(OrderControlTaskStatus.Cancelled, cancel!.Task.Status);
        Assert.Equal(OrderControlTaskStatus.Cancelled, harness.GetTask(taskId)!.Status);
        Assert.Equal(1, harness.EventCount(OrderControlEventType.Cancelled));
        if (scan.Success)
        {
            Assert.Equal(1, harness.EventCount(OrderControlEventType.ScanAccepted));
            Assert.Equal(OrderControlHuStatus.Checked, harness.GetHu("HU-1")!.Status);
        }
        else
        {
            Assert.Equal(OrderControlErrorCodes.TaskCancelled, scan.ErrorCode);
            Assert.Equal(0, harness.EventCount(OrderControlEventType.ScanAccepted));
        }
    }

    private static (TFirst First, TSecond Second) RunConcurrently<TFirst, TSecond>(
        Func<TFirst> first,
        Func<TSecond> second)
    {
        var firstTask = Task.Run(first);
        var secondTask = Task.Run(second);
        Assert.True(Task.WaitAll([firstTask, secondTask], TimeSpan.FromSeconds(10)), "Concurrent operations did not finish.");
        return (firstTask.Result, secondTask.Result);
    }

    private sealed class Harness
    {
        private long _taskId;
        private long _taskOrderId;
        private long _taskHuId;
        private long _taskHuLineId;
        private long _eventId;
        private readonly List<OrderControlTask> _tasks = [];
        private readonly List<OrderControlTaskOrder> _taskOrders = [];
        private readonly List<OrderControlTaskHu> _hus = [];
        private readonly List<OrderControlTaskHuLine> _lines = [];
        private readonly List<OrderControlEvent> _events = [];
        private readonly Dictionary<long, object> _taskLocks = new();
        private readonly object _taskLocksGate = new();
        private readonly AsyncLocal<List<object>?> _heldTaskLocks = new();
        private Barrier? _taskLockBarrier;
        private int _taskLockBarrierWaiters;

        public Harness()
        {
            Store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
                .Callback<Action<IDataStore>>(work =>
                {
                    _heldTaskLocks.Value = [];
                    try
                    {
                        work(Store.Object);
                    }
                    finally
                    {
                        var held = _heldTaskLocks.Value;
                        if (held != null)
                        {
                            for (var i = held.Count - 1; i >= 0; i--)
                            {
                                Monitor.Exit(held[i]);
                            }
                        }

                        _heldTaskLocks.Value = null;
                    }
                });
            Store.Setup(store => store.GetOrder(1)).Returns(() => Order);
            Store.Setup(store => store.GetOrder(It.Is<long>(id => id != 1))).Returns((Order?)null);
            Store.Setup(store => store.FindActiveOrderControlForOrder(It.IsAny<long>()))
                .Returns<long>(FindActiveOrderControlForOrder);
            Store.Setup(store => store.HasActiveOrderControlForOrder(It.IsAny<long>()))
                .Returns<long>(orderId => FindActiveOrderControlForOrder(orderId) != null);
            Store.Setup(store => store.HasStartedOutboundForOrder(It.IsAny<long>())).Returns(false);
            Store.Setup(store => store.IsOutboundHuShipped(It.IsAny<string>())).Returns(false);
            Store.Setup(store => store.LockOrderControlTask(It.IsAny<long>()))
                .Returns<long>(LockOrderControlTask);
            Store.Setup(store => store.GetMaxOrderControlTaskRefSequenceByYear(It.IsAny<int>())).Returns(0);
            Store.Setup(store => store.GetDocs()).Returns(Array.Empty<Doc>());
            Store.Setup(store => store.GetDocsByOrder(It.IsAny<long>())).Returns(Array.Empty<Doc>());
            Store.Setup(store => store.GetDocLines(It.IsAny<long>())).Returns(Array.Empty<DocLine>());
            Store.Setup(store => store.GetProductionPalletsByOrderIds(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns(new Dictionary<long, IReadOnlyList<ProductionPallet>>());
            ConfigureOrderControlStore();
            RefreshReadModelSetups();
        }

        public Mock<IDataStore> Store { get; } = new(MockBehavior.Loose);
        public Order Order { get; private set; } = new()
        {
            Id = 1,
            OrderRef = "080",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerName = "Клиент"
        };

        public IReadOnlyList<OrderReceiptPlanLine> PlanLines { get; set; } =
        [
            new OrderReceiptPlanLine { Id = 1, OrderId = 1, OrderLineId = 10, ItemId = 100, ItemName = "Товар", QtyPlanned = 10, ToHu = "HU-1" }
        ];

        public IReadOnlyList<HuStockRow> StockRows { get; set; } =
        [
            new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 10 }
        ];

        public IReadOnlyList<OrderShipmentLine> ShipmentRemaining { get; set; } =
        [
            new OrderShipmentLine { OrderLineId = 10, ItemId = 100, ItemName = "Товар", QtyOrdered = 10, QtyShipped = 0, QtyRemaining = 10 }
        ];

        public void RefreshReadModelSetups()
        {
            Store.Setup(store => store.GetOrderReceiptPlanLines(1)).Returns(() => PlanLines);
            Store.Setup(store => store.GetHuStockRows()).Returns(() => StockRows);
            Store.Setup(store => store.GetLocations()).Returns([new Location { Id = 1, Code = "FG-01", Name = "ГП" }]);
            Store.Setup(store => store.GetOrderShipmentRemaining(1)).Returns(() => ShipmentRemaining);
        }

        public void UseMixedHu(double qtyB)
        {
            PlanLines =
            [
                new OrderReceiptPlanLine { Id = 1, OrderId = 1, OrderLineId = 10, ItemId = 100, ItemName = "Товар A", QtyPlanned = 10, ToHu = "MIX-1" },
                new OrderReceiptPlanLine { Id = 2, OrderId = 1, OrderLineId = 11, ItemId = 101, ItemName = "Товар B", QtyPlanned = qtyB, ToHu = "MIX-1" }
            ];
            StockRows =
            [
                new HuStockRow { HuCode = "MIX-1", ItemId = 100, LocationId = 1, Qty = 10 },
                new HuStockRow { HuCode = "MIX-1", ItemId = 101, LocationId = 1, Qty = qtyB }
            ];
            ShipmentRemaining =
            [
                new OrderShipmentLine { OrderLineId = 10, ItemId = 100, ItemName = "Товар A", QtyOrdered = 10, QtyShipped = 0, QtyRemaining = 10 },
                new OrderShipmentLine { OrderLineId = 11, ItemId = 101, ItemName = "Товар B", QtyOrdered = qtyB, QtyShipped = 0, QtyRemaining = qtyB }
            ];
            RefreshReadModelSetups();
        }

        public OrderControlTask? GetTask(long taskId) => _tasks.FirstOrDefault(task => task.Id == taskId);

        public OrderControlTaskHu? GetHu(string huCode)
            => _hus.FirstOrDefault(hu => string.Equals(hu.NormalizedHu, huCode, StringComparison.OrdinalIgnoreCase));

        public int EventCount(string eventType)
            => _events.Count(e => string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase));

        public int RequestEventCount(string requestId)
            => _events.Count(e => string.Equals(e.RequestId, requestId, StringComparison.OrdinalIgnoreCase));

        public void EnableTaskLockBarrier()
        {
            _taskLockBarrier = new Barrier(2);
            _taskLockBarrierWaiters = 0;
        }

        private void ConfigureOrderControlStore()
        {
            Store.Setup(store => store.AddOrderControlTask(It.IsAny<OrderControlTask>()))
                .Returns<OrderControlTask>(task =>
                {
                    var saved = Copy(task).WithId(++_taskId);
                    _tasks.Add(saved);
                    return saved.Id;
                });
            Store.Setup(store => store.GetOrderControlTask(It.IsAny<long>()))
                .Returns<long>(id => _tasks.FirstOrDefault(task => task.Id == id));
            Store.Setup(store => store.GetOrderControlTaskOrders(It.IsAny<long>()))
                .Returns<long>(taskId => _taskOrders.Where(order => order.TaskId == taskId).ToArray());
            Store.Setup(store => store.GetOrderControlTaskHus(It.IsAny<long>()))
                .Returns<long>(taskId => _hus.Where(hu => hu.TaskId == taskId).ToArray());
            Store.Setup(store => store.GetOrderControlTaskHuLines(It.IsAny<long>()))
                .Returns<long>(taskId => _lines.Where(line => line.TaskId == taskId).ToArray());
            Store.Setup(store => store.GetOrderControlEvents(It.IsAny<long>()))
                .Returns<long>(taskId => _events.Where(e => e.TaskId == taskId).OrderByDescending(e => e.EventAt).ToArray());
            Store.Setup(store => store.GetOrderControlTaskHuByNormalizedHu(It.IsAny<long>(), It.IsAny<string>()))
                .Returns<long, string>((taskId, hu) => _hus.FirstOrDefault(row => row.TaskId == taskId && string.Equals(row.NormalizedHu, hu, StringComparison.OrdinalIgnoreCase)));
            Store.Setup(store => store.FindOrderControlEventByRequestId(It.IsAny<long>(), It.IsAny<string>()))
                .Returns<long, string>((taskId, requestId) => _events.FirstOrDefault(e => e.TaskId == taskId && string.Equals(e.RequestId, requestId, StringComparison.OrdinalIgnoreCase)));
            Store.Setup(store => store.AddOrderControlTaskOrder(It.IsAny<OrderControlTaskOrder>()))
                .Returns<OrderControlTaskOrder>(order =>
                {
                    var saved = new OrderControlTaskOrder { Id = ++_taskOrderId, TaskId = order.TaskId, OrderId = order.OrderId, OrderRef = order.OrderRef, PartnerName = order.PartnerName, IsActive = order.IsActive };
                    _taskOrders.Add(saved);
                    return saved.Id;
                });
            Store.Setup(store => store.AddOrderControlTaskHu(It.IsAny<OrderControlTaskHu>()))
                .Returns<OrderControlTaskHu>(hu =>
                {
                    var saved = new OrderControlTaskHu { Id = ++_taskHuId, TaskId = hu.TaskId, HuCode = hu.HuCode, NormalizedHu = hu.NormalizedHu, Status = hu.Status, Qty = hu.Qty, ItemSummary = hu.ItemSummary, SnapshotHash = hu.SnapshotHash };
                    _hus.Add(saved);
                    return saved.Id;
                });
            Store.Setup(store => store.AddOrderControlTaskHuLine(It.IsAny<OrderControlTaskHuLine>()))
                .Returns<OrderControlTaskHuLine>(line =>
                {
                    var saved = new OrderControlTaskHuLine { Id = ++_taskHuLineId, TaskHuId = line.TaskHuId, TaskId = line.TaskId, HuCode = line.HuCode, OrderId = line.OrderId, OrderRef = line.OrderRef, OrderLineId = line.OrderLineId, ItemId = line.ItemId, ItemName = line.ItemName, Qty = line.Qty, LocationId = line.LocationId, LocationCode = line.LocationCode, SourceType = line.SourceType };
                    _lines.Add(saved);
                    return saved.Id;
                });
            Store.Setup(store => store.AddOrderControlEvent(It.IsAny<OrderControlEvent>()))
                .Returns<OrderControlEvent>(e =>
                {
                    var saved = new OrderControlEvent { Id = ++_eventId, TaskId = e.TaskId, TaskHuId = e.TaskHuId, EventType = e.EventType, EventAt = e.EventAt, DeviceId = e.DeviceId, OperatorId = e.OperatorId, HuCode = e.HuCode, RequestId = e.RequestId, PayloadJson = e.PayloadJson, ErrorCode = e.ErrorCode, Message = e.Message };
                    _events.Add(saved);
                    return saved.Id;
                });
            Store.Setup(store => store.DeactivateOrderControlTaskOrders(It.IsAny<long>()))
                .Callback<long>(taskId =>
                {
                    for (var i = 0; i < _taskOrders.Count; i++)
                    {
                        var order = _taskOrders[i];
                        if (order.TaskId == taskId)
                        {
                            _taskOrders[i] = new OrderControlTaskOrder
                            {
                                Id = order.Id,
                                TaskId = order.TaskId,
                                OrderId = order.OrderId,
                                OrderRef = order.OrderRef,
                                PartnerName = order.PartnerName,
                                IsActive = false
                            };
                        }
                    }
                });
            Store.Setup(store => store.UpdateOrderControlTaskHuStatus(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .Callback<long, string, DateTime?, string?, string?, string?, string?>((id, status, checkedAt, device, op, error, message) =>
                {
                    var index = _hus.FindIndex(hu => hu.Id == id);
                    var existing = _hus[index];
                    _hus[index] = new OrderControlTaskHu { Id = existing.Id, TaskId = existing.TaskId, HuCode = existing.HuCode, NormalizedHu = existing.NormalizedHu, Status = status, Qty = existing.Qty, ItemSummary = existing.ItemSummary, SnapshotHash = existing.SnapshotHash, CheckedAt = checkedAt ?? existing.CheckedAt, CheckedByDeviceId = device ?? existing.CheckedByDeviceId, CheckedByOperator = op ?? existing.CheckedByOperator, ErrorCode = error, ErrorMessage = message };
                });
            Store.Setup(store => store.UpdateOrderControlTaskProgress(It.IsAny<long>()))
                .Callback<long>(UpdateProgress);
            Store.Setup(store => store.UpdateOrderControlTaskStatus(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .Callback<long, string, DateTime?, DateTime?, DateTime?, string?, string?, string?, string?>((id, status, started, completed, cancelled, cancelledBy, device, error, message) =>
                {
                    var index = _tasks.FindIndex(task => task.Id == id);
                    var task = _tasks[index];
                    _tasks[index] = new OrderControlTask { Id = task.Id, TaskRef = task.TaskRef, Status = status, CreatedAt = task.CreatedAt, CreatedBy = task.CreatedBy, StartedAt = started ?? task.StartedAt, CompletedAt = completed ?? task.CompletedAt, CancelledAt = cancelled ?? task.CancelledAt, CancelledBy = cancelledBy ?? task.CancelledBy, AssignedToDeviceId = device ?? task.AssignedToDeviceId, ExpectedHuCount = task.ExpectedHuCount, CheckedHuCount = task.CheckedHuCount, DiscrepancyHuCount = task.DiscrepancyHuCount, SnapshotHash = task.SnapshotHash, Comment = task.Comment, ErrorCode = error, ErrorMessage = message };
                });
        }

        private OrderControlTaskSummary? FindActiveOrderControlForOrder(long orderId)
        {
            var activeOrder = _taskOrders.FirstOrDefault(order =>
                order.OrderId == orderId
                && order.IsActive
                && _tasks.Any(task => task.Id == order.TaskId && OrderControlTaskStatus.IsActive(task.Status)));
            if (activeOrder == null)
            {
                return null;
            }

            var task = _tasks.First(row => row.Id == activeOrder.TaskId);
            return new OrderControlTaskSummary
            {
                Task = task,
                Orders = _taskOrders.Where(order => order.TaskId == task.Id).ToArray()
            };
        }

        private bool LockOrderControlTask(long taskId)
        {
            if (_tasks.All(task => task.Id != taskId))
            {
                return false;
            }

            var barrier = _taskLockBarrier;
            if (barrier != null)
            {
                var waiter = Interlocked.Increment(ref _taskLockBarrierWaiters);
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)), "Concurrent task lock barrier timed out.");
                if (waiter == 2)
                {
                    Interlocked.Exchange(ref _taskLockBarrier, null)?.Dispose();
                }
            }

            var taskLock = GetTaskLock(taskId);
            Monitor.Enter(taskLock);
            (_heldTaskLocks.Value ??= []).Add(taskLock);
            return true;
        }

        private object GetTaskLock(long taskId)
        {
            lock (_taskLocksGate)
            {
                if (!_taskLocks.TryGetValue(taskId, out var taskLock))
                {
                    taskLock = new object();
                    _taskLocks[taskId] = taskLock;
                }

                return taskLock;
            }
        }

        private void UpdateProgress(long taskId)
        {
            var index = _tasks.FindIndex(task => task.Id == taskId);
            var task = _tasks[index];
            var hus = _hus.Where(hu => hu.TaskId == taskId).ToArray();
            _tasks[index] = new OrderControlTask { Id = task.Id, TaskRef = task.TaskRef, Status = task.Status, CreatedAt = task.CreatedAt, CreatedBy = task.CreatedBy, StartedAt = task.StartedAt, CompletedAt = task.CompletedAt, CancelledAt = task.CancelledAt, CancelledBy = task.CancelledBy, AssignedToDeviceId = task.AssignedToDeviceId, ExpectedHuCount = hus.Length, CheckedHuCount = hus.Count(hu => hu.Status == OrderControlHuStatus.Checked), DiscrepancyHuCount = hus.Count(hu => hu.Status == OrderControlHuStatus.Discrepancy), SnapshotHash = task.SnapshotHash, Comment = task.Comment, ErrorCode = task.ErrorCode, ErrorMessage = task.ErrorMessage };
        }

        private static OrderControlTask Copy(OrderControlTask task) => new()
        {
            TaskRef = task.TaskRef,
            Status = task.Status,
            CreatedAt = task.CreatedAt,
            CreatedBy = task.CreatedBy,
            ExpectedHuCount = task.ExpectedHuCount,
            CheckedHuCount = task.CheckedHuCount,
            DiscrepancyHuCount = task.DiscrepancyHuCount,
            SnapshotHash = task.SnapshotHash,
            Comment = task.Comment
        };
    }
}

file static class OrderControlTestExtensions
{
    public static OrderControlTask WithId(this OrderControlTask task, long id)
    {
        return new OrderControlTask
        {
            Id = id,
            TaskRef = task.TaskRef,
            Status = task.Status,
            CreatedAt = task.CreatedAt,
            CreatedBy = task.CreatedBy,
            ExpectedHuCount = task.ExpectedHuCount,
            CheckedHuCount = task.CheckedHuCount,
            DiscrepancyHuCount = task.DiscrepancyHuCount,
            SnapshotHash = task.SnapshotHash,
            Comment = task.Comment
        };
    }
}
