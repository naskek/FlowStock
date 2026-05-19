using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.WarehouseTasks.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.WarehouseTasks;

public sealed class WarehouseTaskStoreTests
{
    [Fact]
    public void CreateBundle_AddLine_StatusTransition_Works()
    {
        var harness = new WarehouseTaskStoreHarness();
        var store = harness.Store;
        var now = DateTime.Now;

        var bundleId = store.AddWarehouseActionBundle(new WarehouseActionBundle
        {
            BundleRef = WarehouseRefGenerator.GenerateBundleRef(store, now),
            Source = WarehouseBundleSource.Wpf,
            Status = WarehouseBundleStatus.Draft,
            CreatedAt = now,
            CreatedBy = "test"
        });

        var lineId = store.AddWarehouseActionLine(new WarehouseActionLine
        {
            BundleId = bundleId,
            LineNo = store.GetNextWarehouseActionLineNo(bundleId),
            ActionType = WarehouseActionType.MoveHu,
            Status = WarehouseActionLineStatus.Pending,
            HuCode = "HU-0000001",
            PayloadJson = """{"hu_code":"HU-0000001","item_id":1,"qty":1,"to_location_id":2}""",
            CreatedAt = now,
            UpdatedAt = now
        });

        store.UpdateWarehouseActionBundleStatus(
            bundleId,
            WarehouseBundleStatus.Submitted,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var bundle = store.GetWarehouseActionBundle(bundleId);
        var lines = store.GetWarehouseActionLines(bundleId);

        Assert.NotNull(bundle);
        Assert.Equal(WarehouseBundleStatus.Submitted, bundle.Status);
        Assert.Single(lines);
        Assert.Equal(lineId, lines[0].Id);
        Assert.Equal("HU-0000001", lines[0].HuCode);
    }

    [Fact]
    public void TaskEvents_AppendAndRead_Works()
    {
        var harness = new WarehouseTaskStoreHarness();
        var store = harness.Store;
        var bundle = harness.AddBundle(status: WarehouseBundleStatus.InExecution);
        var line = harness.AddLine(bundle.Id, WarehouseActionType.MoveHu, "HU-0000099");

        var taskId = store.AddWarehouseTask(new WarehouseTask
        {
            TaskRef = WarehouseRefGenerator.GenerateTaskRef(store, DateTime.Now),
            BundleId = bundle.Id,
            ActionLineId = line.Id,
            TaskType = WarehouseActionType.MoveHu,
            Status = WarehouseTaskStatus.New,
            CreatedAt = DateTime.Now
        });

        var taskLineId = store.AddWarehouseTaskLine(new WarehouseTaskLine
        {
            TaskId = taskId,
            LineNo = 1,
            ExpectedHuCode = "HU-0000099",
            Status = WarehouseTaskLineStatus.Pending
        });

        var eventAt = DateTime.Now;
        store.AddWarehouseTaskEvent(new WarehouseTaskEvent
        {
            TaskId = taskId,
            TaskLineId = taskLineId,
            EventType = WarehouseTaskEventType.ScanHu,
            EventAt = eventAt,
            HuCode = "HU-0000099",
            DeviceId = "TSD-01"
        });

        store.UpdateWarehouseTaskLineScan(
            taskLineId,
            WarehouseTaskLineStatus.Scanned,
            "HU-0000099",
            null,
            eventAt,
            "TSD-01",
            null,
            null,
            null);

        var events = store.GetWarehouseTaskEvents(taskId);
        var taskLine = store.GetWarehouseTaskLine(taskLineId);

        Assert.Single(events);
        Assert.Equal(WarehouseTaskEventType.ScanHu, events[0].EventType);
        Assert.NotNull(taskLine);
        Assert.Equal(WarehouseTaskLineStatus.Scanned, taskLine.Status);
        Assert.Equal("HU-0000099", taskLine.ScannedHuCode);
    }

    [Fact]
    public void IsHuLockedByActiveWarehouseTask_BlocksSubmittedBundle()
    {
        var harness = new WarehouseTaskStoreHarness();
        var store = harness.Store;
        var bundle = harness.AddBundle(status: WarehouseBundleStatus.Submitted);
        harness.AddLine(bundle.Id, WarehouseActionType.MoveHu, "HU-LOCK-1");

        Assert.True(store.IsHuLockedByActiveWarehouseTask("HU-LOCK-1", null));
        Assert.False(store.IsHuLockedByActiveWarehouseTask("HU-LOCK-1", bundle.Id));
        Assert.False(store.IsHuLockedByActiveWarehouseTask("HU-OTHER", null));
    }

    [Fact]
    public void FindBundleByRef_IsCaseInsensitive()
    {
        var harness = new WarehouseTaskStoreHarness();
        var store = harness.Store;
        var bundle = harness.AddBundle(bundleRef: "BND-2026-000001");

        var found = store.FindWarehouseBundleByRef("bnd-2026-000001");

        Assert.NotNull(found);
        Assert.Equal(bundle.Id, found.Id);
    }
}
