using FlowStock.Core.Models;
using FlowStock.Core.Services.Warehouse;
using FlowStock.Server.Tests.WarehouseTasks.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.WarehouseTasks;

public sealed class WarehouseActionBundleServiceTests
{
    [Fact]
    public void SubmitBundle_RequiresDraftWithLines()
    {
        var harness = new WarehouseBundleServiceHarness();
        var service = harness.BundleService;
        var created = service.CreateBundle(WarehouseBundleSource.Wpf, "test");
        var submit = service.SubmitBundle(created.BundleId!.Value);

        Assert.False(submit.Success);
        Assert.Contains(submit.Errors, error => error.Code == "EMPTY_BUNDLE");
    }

    [Fact]
    public void MoveHu_ApproveCreatesTask_ConfirmExecutionPostsLedger()
    {
        var harness = new WarehouseBundleServiceHarness();
        const string huCode = "HU-MOVE-001";
        var (itemId, fromLoc, toLoc) = harness.SeedMoveScenario(huCode, qty: 5);
        var service = harness.BundleService;
        var tasks = harness.TaskService;

        var created = service.CreateBundle(WarehouseBundleSource.Wpf, "test");
        var bundleId = created.BundleId!.Value;

        service.AddLine(bundleId, new WarehouseBundleLineInput
        {
            ActionType = WarehouseActionType.MoveHu,
            HuCode = huCode,
            ItemId = itemId,
            Qty = 5,
            FromLocationId = fromLoc,
            ToLocationId = toLoc,
            PayloadJson = WarehousePayloadParser.ToJson(new WarehouseMoveHuPayload
            {
                HuCode = huCode,
                ItemId = itemId,
                Qty = 5,
                FromLocationId = fromLoc,
                ToLocationId = toLoc
            })
        });

        Assert.True(service.SubmitBundle(bundleId).Success);
        Assert.True(service.ApproveBundle(bundleId, "supervisor").Success);

        var bundleAfterApprove = harness.Store.GetWarehouseActionBundle(bundleId);
        Assert.Equal(WarehouseBundleStatus.InExecution, bundleAfterApprove!.Status);
        Assert.Empty(harness.LedgerEntries);

        var warehouseTasks = harness.Store.GetWarehouseTasksByBundle(bundleId);
        Assert.Single(warehouseTasks);
        var taskId = warehouseTasks[0].Id;

        Assert.True(tasks.StartTask(taskId, "TSD-01").Success);
        Assert.True(tasks.Scan(taskId, huCode, "HU", "TSD-01").Success);
        Assert.True(tasks.Scan(taskId, "SHIP-01", "LOCATION", "TSD-01").Success);
        Assert.True(tasks.CompleteTask(taskId, "TSD-01").Success);

        var bundleExecuted = harness.Store.GetWarehouseActionBundle(bundleId);
        Assert.Equal(WarehouseBundleStatus.Executed, bundleExecuted!.Status);

        var confirm = service.ConfirmExecution(bundleId, "supervisor");
        Assert.True(confirm.Success);
        Assert.Equal(2, harness.LedgerEntries.Count);
        Assert.Contains(harness.LedgerEntries, entry => entry.QtyDelta < 0);
        Assert.Contains(harness.LedgerEntries, entry => entry.QtyDelta > 0);

        var confirmAgain = service.ConfirmExecution(bundleId, "supervisor");
        Assert.True(confirmAgain.Success);
        Assert.Equal(2, harness.LedgerEntries.Count);
    }

    [Fact]
    public void ConfirmExecution_IsIdempotentWhenCompleted()
    {
        var harness = new WarehouseBundleServiceHarness();
        var service = harness.BundleService;
        var created = service.CreateBundle(WarehouseBundleSource.Wpf, "test");
        var bundleId = created.BundleId!.Value;

        harness.Store.UpdateWarehouseActionBundleStatus(
            bundleId,
            WarehouseBundleStatus.Completed,
            null,
            null,
            null,
            DateTime.Now,
            null,
            null,
            null,
            null);

        var result = service.ConfirmExecution(bundleId);
        Assert.True(result.Success);
        Assert.Equal(WarehouseBundleStatus.Completed, result.Status);
    }

    [Fact]
    public void HuLock_BlocksSecondSubmittedBundle()
    {
        var harness = new WarehouseBundleServiceHarness();
        const string huCode = "HU-LOCK-002";
        var (itemId, fromLoc, toLoc) = harness.SeedMoveScenario(huCode);
        var service = harness.BundleService;

        long CreateSubmittedBundle()
        {
            var created = service.CreateBundle(WarehouseBundleSource.Wpf, "test");
            service.AddLine(created.BundleId!.Value, new WarehouseBundleLineInput
            {
                ActionType = WarehouseActionType.MoveHu,
                HuCode = huCode,
                ItemId = itemId,
                ToLocationId = toLoc,
                FromLocationId = fromLoc,
                Qty = 1
            });
            service.SubmitBundle(created.BundleId!.Value);
            return created.BundleId!.Value;
        }

        CreateSubmittedBundle();
        var second = service.CreateBundle(WarehouseBundleSource.Wpf, "test");
        var add = service.AddLine(second.BundleId!.Value, new WarehouseBundleLineInput
        {
            ActionType = WarehouseActionType.MoveHu,
            HuCode = huCode,
            ItemId = itemId,
            ToLocationId = toLoc,
            FromLocationId = fromLoc,
            Qty = 1
        });

        Assert.False(add.Success);
        Assert.Contains(add.Errors, issue => issue.Code == "HU_LOCKED");
    }
}
