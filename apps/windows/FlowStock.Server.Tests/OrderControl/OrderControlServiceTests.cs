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

    [Fact]
    public void ConcurrentCreateControlAndOutboundDraft_PreservesMutualExclusion()
    {
        var harness = new Harness();
        var control = new OrderControlService(harness.Store.Object);
        var documents = new DocumentService(harness.Store.Object);
        harness.EnableOrderLockBarrier();

        var (controlResult, outboundResult) = RunConcurrently(
            () => control.Create([1], "tester", null),
            () => TryCreateOutboundDraft(documents, 1));

        var hasActiveControl = harness.Store.Object.FindActiveOrderControlForOrder(1) != null;
        var hasDraft = harness.OutboundDraftCount(1) > 0;
        Assert.NotEqual(hasActiveControl, hasDraft);
        Assert.Equal(hasActiveControl, controlResult.Success);
        Assert.Equal(hasDraft, outboundResult.Success);
    }

    [Fact]
    public void ConcurrentCreateControlAndTsdOutboundScan_DoNotBothCreateControlAndDocLines()
    {
        var harness = new Harness();
        var control = new OrderControlService(harness.Store.Object);
        var outbound = new OutboundPickingService(harness.Store.Object, new DocumentService(harness.Store.Object));
        harness.EnableOrderLockBarrier();

        var (controlResult, scanResult) = RunConcurrently(
            () => control.Create([1], "tester", null),
            () => outbound.Scan(1, "HU-1", "TSD-1"));

        var hasActiveControl = harness.Store.Object.FindActiveOrderControlForOrder(1) != null;
        var hasOutboundLines = harness.OutboundLineCount(1) > 0;
        Assert.False(hasActiveControl && hasOutboundLines);
        Assert.Equal(hasActiveControl, controlResult.Success);
        Assert.Equal(hasOutboundLines, scanResult.Success);
    }

    [Fact]
    public void ConcurrentCreateControlAndCloseOutbound_PreservesInvariant()
    {
        var harness = new Harness();
        var docId = harness.AddOutboundDraft(1);
        var control = new OrderControlService(harness.Store.Object);
        var documents = new DocumentService(harness.Store.Object);

        var (controlResult, closeResult) = RunConcurrently(
            () => control.Create([1], "tester", null),
            () => documents.TryCloseDoc(docId, allowNegative: false));

        Assert.False(controlResult.Success);
        Assert.Null(harness.Store.Object.FindActiveOrderControlForOrder(1));
        Assert.True(closeResult.Success || closeResult.Errors.Count > 0);
        Assert.False(harness.Store.Object.FindActiveOrderControlForOrder(1) != null && harness.IsOutboundClosed(docId));
    }

    [Fact]
    public void ConcurrentCreateControlForDifferentOrders_UsesDistinctTaskRefs()
    {
        var harness = new Harness();
        harness.AddReadyOrder(2, "081", 20, 200, "HU-2", 7);
        var control = new OrderControlService(harness.Store.Object);

        var (first, second) = RunConcurrently(
            () => control.Create([1], "tester", null),
            () => control.Create([2], "tester", null));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.Task!.Task.TaskRef, second.Task!.Task.TaskRef);
    }

    private static OperationResult<long> TryCreateOutboundDraft(DocumentService documents, long orderId)
    {
        try
        {
            var docId = documents.CreateDoc(
                DocType.Outbound,
                $"OUT-RACE-{Guid.NewGuid():N}",
                null,
                null,
                null,
                null,
                orderId,
                hydrateOrderLines: false);
            return OperationResult<long>.Ok(docId);
        }
        catch (Exception ex)
        {
            return OperationResult<long>.Fail(ex.Message);
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

    private sealed class OperationResult<T>
    {
        public bool Success { get; init; }
        public T? Value { get; init; }
        public string? Error { get; init; }

        public static OperationResult<T> Ok(T value) => new() { Success = true, Value = value };
        public static OperationResult<T> Fail(string error) => new() { Success = false, Error = error };
    }

    private sealed class Harness
    {
        private long _taskId;
        private long _taskOrderId;
        private long _taskHuId;
        private long _taskHuLineId;
        private long _eventId;
        private long _docId;
        private long _docLineId;
        private readonly List<OrderControlTask> _tasks = [];
        private readonly List<OrderControlTaskOrder> _taskOrders = [];
        private readonly List<OrderControlTaskHu> _hus = [];
        private readonly List<OrderControlTaskHuLine> _lines = [];
        private readonly List<OrderControlEvent> _events = [];
        private readonly Dictionary<long, Order> _orders = new();
        private readonly Dictionary<long, List<OrderLine>> _orderLines = new();
        private readonly Dictionary<long, IReadOnlyList<OrderReceiptPlanLine>> _planLinesByOrder = new();
        private readonly Dictionary<long, IReadOnlyList<OrderShipmentLine>> _shipmentRemainingByOrder = new();
        private readonly List<Doc> _docs = [];
        private readonly List<DocLine> _docLines = [];
        private readonly Dictionary<long, object> _taskLocks = new();
        private readonly Dictionary<long, object> _orderLocks = new();
        private readonly object _taskRefLock = new();
        private readonly object _taskLocksGate = new();
        private readonly object _orderLocksGate = new();
        private readonly AsyncLocal<List<object>?> _heldTaskLocks = new();
        private readonly AsyncLocal<List<object>?> _heldOrderLocks = new();
        private Barrier? _taskLockBarrier;
        private Barrier? _orderLockBarrier;
        private int _taskLockBarrierWaiters;
        private int _orderLockBarrierWaiters;

        public Harness()
        {
            _orders[1] = Order;
            _orderLines[1] =
            [
                new OrderLine { Id = 10, OrderId = 1, ItemId = 100, QtyOrdered = 10, ProductionPurpose = ProductionLinePurpose.CustomerOrder }
            ];
            _planLinesByOrder[1] =
            [
                new OrderReceiptPlanLine { Id = 1, OrderId = 1, OrderLineId = 10, ItemId = 100, ItemName = "Товар", QtyPlanned = 10, ToHu = "HU-1" }
            ];
            _shipmentRemainingByOrder[1] =
            [
                new OrderShipmentLine { OrderLineId = 10, ItemId = 100, ItemName = "Товар", QtyOrdered = 10, QtyShipped = 0, QtyRemaining = 10 }
            ];

            Store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
                .Callback<Action<IDataStore>>(work =>
                {
                    _heldTaskLocks.Value = [];
                    _heldOrderLocks.Value = [];
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

                        var heldOrders = _heldOrderLocks.Value;
                        if (heldOrders != null)
                        {
                            for (var i = heldOrders.Count - 1; i >= 0; i--)
                            {
                                Monitor.Exit(heldOrders[i]);
                            }
                        }

                        _heldTaskLocks.Value = null;
                        _heldOrderLocks.Value = null;
                    }
                });
            Store.Setup(store => store.GetOrder(It.IsAny<long>()))
                .Returns<long>(id => _orders.TryGetValue(id, out var order) ? order : null);
            Store.Setup(store => store.GetOrders()).Returns(() => _orders.Values.OrderBy(order => order.Id).ToArray());
            Store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
                .Returns<long>(orderId => _orderLines.TryGetValue(orderId, out var lines) ? lines : Array.Empty<OrderLine>());
            Store.Setup(store => store.GetShippedTotalsByOrderLine(It.IsAny<long>()))
                .Returns(new Dictionary<long, double>());
            Store.Setup(store => store.FindActiveOrderControlForOrder(It.IsAny<long>()))
                .Returns<long>(FindActiveOrderControlForOrder);
            Store.Setup(store => store.HasActiveOrderControlForOrder(It.IsAny<long>()))
                .Returns<long>(orderId => FindActiveOrderControlForOrder(orderId) != null);
            Store.Setup(store => store.HasStartedOutboundForOrder(It.IsAny<long>()))
                .Returns<long>(orderId => _docs.Any(doc => doc.OrderId == orderId && doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft));
            Store.Setup(store => store.IsOutboundHuShipped(It.IsAny<string>())).Returns(false);
            Store.Setup(store => store.LockOrdersForUpdate(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns<IReadOnlyCollection<long>>(LockOrdersForUpdate);
            Store.Setup(store => store.LockOrderControlTask(It.IsAny<long>()))
                .Returns<long>(LockOrderControlTask);
            Store.Setup(store => store.GetMaxOrderControlTaskRefSequenceByYear(It.IsAny<int>()))
                .Returns<int>(GetMaxOrderControlTaskRefSequenceByYear);
            Store.Setup(store => store.GetMaxDocRefSequenceByYear(It.IsAny<int>()))
                .Returns<int>(GetMaxDocRefSequenceByYear);
            Store.Setup(store => store.IsDocRefSequenceTaken(It.IsAny<int>(), It.IsAny<int>()))
                .Returns<int, int>(IsDocRefSequenceTaken);
            Store.Setup(store => store.FindDocByRef(It.IsAny<string>()))
                .Returns<string>(docRef => _docs.FirstOrDefault(doc => string.Equals(doc.DocRef, docRef, StringComparison.OrdinalIgnoreCase)));
            Store.Setup(store => store.GetDoc(It.IsAny<long>()))
                .Returns<long>(docId => _docs.FirstOrDefault(doc => doc.Id == docId));
            Store.Setup(store => store.GetDocs()).Returns(() => _docs.ToArray());
            Store.Setup(store => store.GetDocsByOrder(It.IsAny<long>()))
                .Returns<long>(orderId => _docs.Where(doc => doc.OrderId == orderId).OrderBy(doc => doc.Id).ToArray());
            Store.Setup(store => store.GetDocLines(It.IsAny<long>()))
                .Returns<long>(docId => _docLines.Where(line => line.DocId == docId).OrderBy(line => line.Id).ToArray());
            Store.Setup(store => store.GetItems(It.IsAny<string?>()))
                .Returns(() => [new Item { Id = 100, Name = "Товар", IsActive = true }]);
            Store.Setup(store => store.GetPartner(It.IsAny<long>()))
                .Returns<long>(id => new Partner { Id = id, Code = $"P-{id}", Name = $"Partner {id}" });
            Store.Setup(store => store.GetLedgerBalance(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>()))
                .Returns<long, long, string?>((itemId, locationId, hu) => StockRows
                    .Where(row => row.ItemId == itemId
                                  && row.LocationId == locationId
                                  && string.Equals(row.HuCode, hu, StringComparison.OrdinalIgnoreCase))
                    .Sum(row => row.Qty));
            Store.Setup(store => store.AddDoc(It.IsAny<Doc>()))
                .Returns<Doc>(AddDoc);
            Store.Setup(store => store.AddDocLine(It.IsAny<DocLine>()))
                .Returns<DocLine>(AddDocLine);
            Store.Setup(store => store.UpdateDocStatus(It.IsAny<long>(), It.IsAny<DocStatus>(), It.IsAny<DateTime?>()))
                .Callback<long, DocStatus, DateTime?>(UpdateDocStatus);
            Store.Setup(store => store.AddLedgerEntry(It.IsAny<LedgerEntry>()));
            Store.Setup(store => store.UpdateOrderStatus(It.IsAny<long>(), It.IsAny<OrderStatus>()))
                .Callback<long, OrderStatus>(UpdateOrderStatus);
            Store.Setup(store => store.UpdateDocLineOrderLineId(It.IsAny<long>(), It.IsAny<long?>()))
                .Callback<long, long?>(UpdateDocLineOrderLineId);
            Store.Setup(store => store.HasProductionPallets(It.IsAny<long>())).Returns(false);
            Store.Setup(store => store.GetProductionPalletsByOrderIds(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns(new Dictionary<long, IReadOnlyList<ProductionPallet>>());
            Store.Setup(store => store.GetDocsByOrderIds(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns<IReadOnlyCollection<long>>(ids => ids.ToDictionary(
                    id => id,
                    id => (IReadOnlyList<Doc>)_docs.Where(doc => doc.OrderId == id).ToArray()));
            Store.Setup(store => store.GetDocLinesByDocIds(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns<IReadOnlyCollection<long>>(ids => ids.ToDictionary(
                    id => id,
                    id => (IReadOnlyList<DocLine>)_docLines.Where(line => line.DocId == id).ToArray()));
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

        public IReadOnlyList<OrderReceiptPlanLine> PlanLines
        {
            get => _planLinesByOrder.TryGetValue(1, out var lines)
                ? lines
                : Array.Empty<OrderReceiptPlanLine>();
            set => _planLinesByOrder[1] = value;
        }

        public IReadOnlyList<HuStockRow> StockRows { get; set; } =
        [
            new HuStockRow { HuCode = "HU-1", ItemId = 100, LocationId = 1, Qty = 10 }
        ];

        public IReadOnlyList<OrderShipmentLine> ShipmentRemaining
        {
            get => _shipmentRemainingByOrder.TryGetValue(1, out var lines)
                ? lines
                : Array.Empty<OrderShipmentLine>();
            set => _shipmentRemainingByOrder[1] = value;
        }

        public void RefreshReadModelSetups()
        {
            Store.Setup(store => store.GetOrderReceiptPlanLines(It.IsAny<long>()))
                .Returns<long>(orderId => _planLinesByOrder.TryGetValue(orderId, out var lines) ? lines : Array.Empty<OrderReceiptPlanLine>());
            Store.Setup(store => store.GetOrderReceiptPlanLinesByOrderIds(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns<IReadOnlyCollection<long>>(ids => ids.ToDictionary(
                    id => id,
                    id => _planLinesByOrder.TryGetValue(id, out var lines)
                        ? lines
                        : (IReadOnlyList<OrderReceiptPlanLine>)Array.Empty<OrderReceiptPlanLine>()));
            Store.Setup(store => store.GetHuStockRows()).Returns(() => StockRows);
            Store.Setup(store => store.GetLocations()).Returns([new Location { Id = 1, Code = "FG-01", Name = "ГП" }]);
            Store.Setup(store => store.GetOrderShipmentRemaining(It.IsAny<long>()))
                .Returns<long>(orderId => _shipmentRemainingByOrder.TryGetValue(orderId, out var lines) ? lines : Array.Empty<OrderShipmentLine>());
            Store.Setup(store => store.GetOrderShipmentRemainingByOrderIds(It.IsAny<IReadOnlyCollection<long>>()))
                .Returns<IReadOnlyCollection<long>>(ids => ids.ToDictionary(
                    id => id,
                    id => _shipmentRemainingByOrder.TryGetValue(id, out var lines)
                        ? lines
                        : (IReadOnlyList<OrderShipmentLine>)Array.Empty<OrderShipmentLine>()));
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

        public int OutboundDraftCount(long orderId)
            => _docs.Count(doc => doc.OrderId == orderId && doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft);

        public int OutboundLineCount(long orderId)
        {
            var docIds = _docs
                .Where(doc => doc.OrderId == orderId && doc.Type == DocType.Outbound)
                .Select(doc => doc.Id)
                .ToHashSet();
            return _docLines.Count(line => docIds.Contains(line.DocId));
        }

        public bool IsOutboundClosed(long docId)
            => _docs.Any(doc => doc.Id == docId && doc.Type == DocType.Outbound && doc.Status == DocStatus.Closed);

        public void EnableTaskLockBarrier()
        {
            _taskLockBarrier = new Barrier(2);
            _taskLockBarrierWaiters = 0;
        }

        public void EnableOrderLockBarrier()
        {
            _orderLockBarrier = new Barrier(2);
            _orderLockBarrierWaiters = 0;
        }

        public void AddReadyOrder(long orderId, string orderRef, long orderLineId, long itemId, string huCode, double qty)
        {
            _orders[orderId] = new Order
            {
                Id = orderId,
                OrderRef = orderRef,
                Type = OrderType.Customer,
                Status = OrderStatus.Accepted,
                PartnerName = $"Клиент {orderRef}"
            };
            _orderLines[orderId] =
            [
                new OrderLine
                {
                    Id = orderLineId,
                    OrderId = orderId,
                    ItemId = itemId,
                    QtyOrdered = qty,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                }
            ];
            _planLinesByOrder[orderId] =
            [
                new OrderReceiptPlanLine
                {
                    Id = orderId,
                    OrderId = orderId,
                    OrderLineId = orderLineId,
                    ItemId = itemId,
                    ItemName = $"Товар {itemId}",
                    QtyPlanned = qty,
                    ToHu = huCode
                }
            ];
            _shipmentRemainingByOrder[orderId] =
            [
                new OrderShipmentLine
                {
                    OrderLineId = orderLineId,
                    ItemId = itemId,
                    ItemName = $"Товар {itemId}",
                    QtyOrdered = qty,
                    QtyShipped = 0,
                    QtyRemaining = qty
                }
            ];
            StockRows = StockRows.Concat([
                new HuStockRow { HuCode = huCode, ItemId = itemId, LocationId = 1, Qty = qty }
            ]).ToArray();
            RefreshReadModelSetups();
        }

        public long AddOutboundDraft(long orderId, string huCode = "HU-1")
        {
            var order = _orders[orderId];
            var docId = AddDoc(new Doc
            {
                DocRef = $"OUT-TEST-{_docId + 1:000000}",
                Type = DocType.Outbound,
                Status = DocStatus.Draft,
                CreatedAt = DateTime.Now,
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                PartnerId = order.PartnerId
            });
            var planLine = _planLinesByOrder[orderId].First();
            AddDocLine(new DocLine
            {
                DocId = docId,
                OrderLineId = planLine.OrderLineId,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ItemId = planLine.ItemId,
                Qty = planLine.QtyPlanned,
                FromLocationId = 1,
                FromHu = huCode
            });
            return docId;
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

        private bool LockOrdersForUpdate(IReadOnlyCollection<long> orderIds)
        {
            var normalized = orderIds?.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray() ?? Array.Empty<long>();
            if (!normalized.All(id => _orders.ContainsKey(id)))
            {
                return false;
            }

            var barrier = _orderLockBarrier;
            if (barrier != null)
            {
                var waiter = Interlocked.Increment(ref _orderLockBarrierWaiters);
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)), "Concurrent order lock barrier timed out.");
                if (waiter == 2)
                {
                    Interlocked.Exchange(ref _orderLockBarrier, null)?.Dispose();
                }
            }

            foreach (var orderId in normalized)
            {
                var orderLock = GetOrderLock(orderId);
                Monitor.Enter(orderLock);
                (_heldOrderLocks.Value ??= []).Add(orderLock);
            }

            return true;
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

        private object GetOrderLock(long orderId)
        {
            lock (_orderLocksGate)
            {
                if (!_orderLocks.TryGetValue(orderId, out var orderLock))
                {
                    orderLock = new object();
                    _orderLocks[orderId] = orderLock;
                }

                return orderLock;
            }
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

        private int GetMaxOrderControlTaskRefSequenceByYear(int year)
        {
            Monitor.Enter(_taskRefLock);
            (_heldTaskLocks.Value ??= []).Add(_taskRefLock);
            var token = $"-{year}-";
            return _tasks
                .Select(task => task.TaskRef)
                .Where(taskRef => taskRef.Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(taskRef => int.TryParse(taskRef.Split('-')[^1], out var sequence) ? sequence : 0)
                .DefaultIfEmpty(0)
                .Max();
        }

        private int GetMaxDocRefSequenceByYear(int year)
        {
            var token = $"-{year}-";
            return _docs
                .Select(doc => doc.DocRef)
                .Where(docRef => docRef.Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(docRef => int.TryParse(docRef.Split('-')[^1], out var sequence) ? sequence : 0)
                .DefaultIfEmpty(0)
                .Max();
        }

        private bool IsDocRefSequenceTaken(int year, int sequence)
        {
            var suffix = $"-{year}-{sequence:000000}";
            return _docs.Any(doc => doc.DocRef.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private long AddDoc(Doc doc)
        {
            var saved = new Doc
            {
                Id = ++_docId,
                DocRef = doc.DocRef,
                Type = doc.Type,
                Status = doc.Status,
                CreatedAt = doc.CreatedAt,
                ClosedAt = doc.ClosedAt,
                PartnerId = doc.PartnerId,
                OrderId = doc.OrderId,
                OrderRef = doc.OrderRef,
                ShippingRef = doc.ShippingRef,
                Comment = doc.Comment
            };
            _docs.Add(saved);
            return saved.Id;
        }

        private long AddDocLine(DocLine line)
        {
            var saved = new DocLine
            {
                Id = ++_docLineId,
                DocId = line.DocId,
                OrderLineId = line.OrderLineId,
                ProductionPurpose = line.ProductionPurpose,
                ItemId = line.ItemId,
                Qty = line.Qty,
                QtyInput = line.QtyInput,
                UomCode = line.UomCode,
                FromLocationId = line.FromLocationId,
                ToLocationId = line.ToLocationId,
                FromHu = line.FromHu,
                ToHu = line.ToHu
            };
            _docLines.Add(saved);
            return saved.Id;
        }

        private void UpdateDocStatus(long docId, DocStatus status, DateTime? closedAt)
        {
            var index = _docs.FindIndex(doc => doc.Id == docId);
            var doc = _docs[index];
            _docs[index] = new Doc
            {
                Id = doc.Id,
                DocRef = doc.DocRef,
                Type = doc.Type,
                Status = status,
                CreatedAt = doc.CreatedAt,
                ClosedAt = closedAt ?? doc.ClosedAt,
                PartnerId = doc.PartnerId,
                OrderId = doc.OrderId,
                OrderRef = doc.OrderRef,
                ShippingRef = doc.ShippingRef,
                Comment = doc.Comment
            };
        }

        private void UpdateOrderStatus(long orderId, OrderStatus status)
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return;
            }

            _orders[orderId] = new Order
            {
                Id = order.Id,
                OrderRef = order.OrderRef,
                Type = order.Type,
                Status = status,
                PartnerId = order.PartnerId,
                PartnerName = order.PartnerName,
                CreatedAt = order.CreatedAt
            };
        }

        private void UpdateDocLineOrderLineId(long docLineId, long? orderLineId)
        {
            var index = _docLines.FindIndex(line => line.Id == docLineId);
            if (index < 0)
            {
                return;
            }

            var line = _docLines[index];
            _docLines[index] = new DocLine
            {
                Id = line.Id,
                DocId = line.DocId,
                OrderLineId = orderLineId,
                ProductionPurpose = line.ProductionPurpose,
                ItemId = line.ItemId,
                Qty = line.Qty,
                QtyInput = line.QtyInput,
                UomCode = line.UomCode,
                FromLocationId = line.FromLocationId,
                ToLocationId = line.ToLocationId,
                FromHu = line.FromHu,
                ToHu = line.ToHu
            };
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
