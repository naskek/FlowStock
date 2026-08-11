using FlowStock.App;

namespace FlowStock.Server.Tests.Orders;

public sealed class HuAssignmentManagementSessionTests
{
    [Fact]
    public void StageBind_FreeHu_BuildsExpectedStateAndFinalLine()
    {
        var session = CreateSession(
            hus: [Hu("HU-FREE", qty: 5)],
            targets: [Target(orderId: 10, lineId: 100, maxAdditional: 5)]);

        Assert.True(session.StageBind(session.FindHu("hu-free"), session.FindTargetLine(100), out var message), message);

        var request = session.BuildApplyRequest();
        var state = Assert.Single(request.ExpectedHuStates);
        var line = Assert.Single(request.Lines);
        Assert.Null(state.ExpectedOrderId);
        Assert.Null(state.ExpectedOrderLineId);
        Assert.Equal("HU-FREE", state.HuCode);
        Assert.Empty(line.ExpectedBoundHuCodes);
        Assert.Equal(["HU-FREE"], line.FinalHuCodes);
    }

    [Fact]
    public void StageDetach_BoundHu_BuildsExplicitDetach()
    {
        var session = CreateSession(
            hus: [Hu("HU-OLD", qty: 5, assignment: Assignment(10, 100))],
            targets: [Target(orderId: 10, lineId: 100, current: ["HU-OLD"], currentQty: 5)]);

        Assert.True(session.StageDetach(session.FindHu("HU-OLD"), out var message), message);

        var request = session.BuildApplyRequest();
        var line = Assert.Single(request.Lines);
        Assert.Equal(["HU-OLD"], line.ExpectedBoundHuCodes);
        Assert.Empty(line.FinalHuCodes);
        Assert.Equal("Отвязать", Assert.Single(session.Changes).ChangeKind);
    }

    [Fact]
    public void StageMove_BetweenOrders_BuildsMultiOrderBatch()
    {
        var session = CreateSession(
            hus:
            [
                Hu("HU-MOVE", qty: 5, assignment: Assignment(10, 100)),
                Hu("HU-KEEP", qty: 3, assignment: Assignment(20, 200))
            ],
            targets:
            [
                Target(orderId: 10, lineId: 100, current: ["HU-MOVE"], currentQty: 5),
                Target(orderId: 20, lineId: 200, current: ["HU-KEEP"], currentQty: 3, maxAdditional: 5)
            ]);

        Assert.True(session.StageBind(session.FindHu("HU-MOVE"), session.FindTargetLine(200), out var message), message);

        var request = session.BuildApplyRequest();
        Assert.Equal(new List<long> { 100L, 200L }, request.Lines.Select(line => line.OrderLineId).Order().ToList());
        Assert.Equal(2, request.Lines.Select(line => line.OrderId).Distinct().Count());
        Assert.Equal(["HU-MOVE"], request.Lines.Single(line => line.OrderLineId == 100).ExpectedBoundHuCodes);
        Assert.Empty(request.Lines.Single(line => line.OrderLineId == 100).FinalHuCodes);
        Assert.Equal(["HU-KEEP"], request.Lines.Single(line => line.OrderLineId == 200).ExpectedBoundHuCodes);
        Assert.Equal(["HU-KEEP", "HU-MOVE"], request.Lines.Single(line => line.OrderLineId == 200).FinalHuCodes);
    }

    [Fact]
    public void CancelChange_ReturnsHuToOriginalAndRemovesChangeRow()
    {
        var session = CreateSession(
            hus: [Hu("HU-OLD", qty: 5, assignment: Assignment(10, 100))],
            targets:
            [
                Target(orderId: 10, lineId: 100, current: ["HU-OLD"], currentQty: 5),
                Target(orderId: 20, lineId: 200, maxAdditional: 5)
            ]);
        var hu = session.FindHu("HU-OLD");
        Assert.True(session.StageBind(hu, session.FindTargetLine(200), out _));

        Assert.True(session.CancelChange(hu, out var message), message);

        Assert.Empty(session.Changes);
        Assert.Empty(session.BuildApplyRequest().Lines);
        Assert.Equal(100, hu!.FutureOrderLineId);
    }

    [Fact]
    public void StageBind_ReturnToOriginalTarget_RemovesChangeRow()
    {
        var session = CreateSession(
            hus: [Hu("HU-OLD", qty: 5, assignment: Assignment(10, 100))],
            targets:
            [
                Target(orderId: 10, lineId: 100, current: ["HU-OLD"], currentQty: 5),
                Target(orderId: 20, lineId: 200, maxAdditional: 5)
            ]);
        var hu = session.FindHu("HU-OLD");
        Assert.True(session.StageBind(hu, session.FindTargetLine(200), out _));

        Assert.True(session.StageBind(hu, session.FindTargetLine(100), out var message), message);

        Assert.Empty(session.Changes);
        Assert.Empty(session.BuildApplyRequest().Lines);
    }

    [Fact]
    public void BuildApplyRequest_PreservesExistingTargetHu()
    {
        var session = CreateSession(
            hus:
            [
                Hu("HU-FREE", qty: 5),
                Hu("HU-KEEP", qty: 3, assignment: Assignment(10, 100))
            ],
            targets: [Target(orderId: 10, lineId: 100, current: ["HU-KEEP"], currentQty: 3, maxAdditional: 5)]);

        Assert.True(session.StageBind(session.FindHu("HU-FREE"), session.FindTargetLine(100), out var message), message);

        var line = Assert.Single(session.BuildApplyRequest().Lines);
        Assert.Equal(["HU-KEEP"], line.ExpectedBoundHuCodes);
        Assert.Equal(["HU-FREE", "HU-KEEP"], line.FinalHuCodes);
    }

    [Fact]
    public void Capacity_RecalculatedAfterDetachAllowsReplacement()
    {
        var session = CreateSession(
            hus:
            [
                Hu("HU-OLD", qty: 10, assignment: Assignment(10, 100)),
                Hu("HU-NEW", qty: 10)
            ],
            targets: [Target(orderId: 10, lineId: 100, current: ["HU-OLD"], currentQty: 10, maxAdditional: 0)]);

        Assert.False(session.StageBind(session.FindHu("HU-NEW"), session.FindTargetLine(100), out _));
        Assert.True(session.StageDetach(session.FindHu("HU-OLD"), out _));
        Assert.True(session.StageBind(session.FindHu("HU-NEW"), session.FindTargetLine(100), out var message), message);

        var line = Assert.Single(session.BuildApplyRequest().Lines);
        Assert.Equal(["HU-OLD"], line.ExpectedBoundHuCodes);
        Assert.Equal(["HU-NEW"], line.FinalHuCodes);
    }

    [Fact]
    public void StageBind_OneHuCanOnlyHaveOneFutureTarget()
    {
        var session = CreateSession(
            hus: [Hu("HU-ONE", qty: 5)],
            targets:
            [
                Target(orderId: 10, lineId: 100, maxAdditional: 5),
                Target(orderId: 20, lineId: 200, maxAdditional: 5)
            ]);
        var hu = session.FindHu("HU-ONE");
        Assert.True(session.StageBind(hu, session.FindTargetLine(100), out _));

        Assert.True(session.StageBind(hu, session.FindTargetLine(200), out var message), message);

        var request = session.BuildApplyRequest();
        var line = Assert.Single(request.Lines);
        Assert.Equal(200, line.OrderLineId);
        Assert.Equal(["HU-ONE"], line.FinalHuCodes);
        Assert.Single(request.ExpectedHuStates);
        Assert.Single(session.Changes);
    }

    [Fact]
    public void TryChangeItem_WithStagedChanges_DoesNotMutateUntilDiscard()
    {
        var session = CreateSession(
            itemId: 1,
            hus: [Hu("HU-A", qty: 5)],
            targets: [Target(orderId: 10, lineId: 100, maxAdditional: 5)]);
        Assert.True(session.StageBind(session.FindHu("HU-A"), session.FindTargetLine(100), out _));

        var changed = session.TryChangeItem(
            Page(itemId: 2, itemName: "Другой", Hu("HU-B", itemId: 2, qty: 4)),
            [Target(orderId: 20, lineId: 200, itemId: 2, maxAdditional: 4)],
            discardStagedChanges: false,
            out var message);

        Assert.False(changed);
        Assert.Contains("неподтвержден", message);
        Assert.Equal(1, session.ItemId);
        Assert.NotNull(session.FindHu("HU-A"));
        Assert.Null(session.FindHu("HU-B"));

        Assert.True(session.TryChangeItem(
            Page(itemId: 2, itemName: "Другой", Hu("HU-B", itemId: 2, qty: 4)),
            [Target(orderId: 20, lineId: 200, itemId: 2, maxAdditional: 4)],
            discardStagedChanges: true,
            out message), message);
        Assert.Equal(2, session.ItemId);
        Assert.Null(session.FindHu("HU-A"));
        Assert.NotNull(session.FindHu("HU-B"));
    }

    [Fact]
    public void TryRefresh_WithStagedChanges_DoesNotMutateSavedState()
    {
        var session = CreateSession(
            hus: [Hu("HU-A", qty: 5)],
            targets: [Target(orderId: 10, lineId: 100, maxAdditional: 5)]);
        Assert.True(session.StageBind(session.FindHu("HU-A"), session.FindTargetLine(100), out _));

        var refreshed = session.TryRefreshFrom(
            Page(Hu("HU-B", qty: 5)),
            [Target(orderId: 10, lineId: 100, maxAdditional: 5)],
            discardStagedChanges: false,
            out _);

        Assert.False(refreshed);
        Assert.NotNull(session.FindHu("HU-A"));
        Assert.Null(session.FindHu("HU-B"));
        Assert.Single(session.Changes);
    }

    [Fact]
    public void FailedSave_DoesNotMutateOriginalState()
    {
        var session = CreateSession(
            hus: [Hu("HU-A", qty: 5)],
            targets: [Target(orderId: 10, lineId: 100, maxAdditional: 5)]);
        var hu = session.FindHu("HU-A");
        Assert.True(session.StageBind(hu, session.FindTargetLine(100), out _));

        _ = session.BuildApplyRequest();

        Assert.Null(hu!.OriginalOrderId);
        Assert.Null(hu.OriginalOrderLineId);
        Assert.Equal(100, hu.FutureOrderLineId);
        Assert.Single(session.Changes);
    }

    [Fact]
    public void MarkSaveSuccess_CommitsFutureAsOriginal()
    {
        var session = CreateSession(
            hus: [Hu("HU-A", qty: 5)],
            targets: [Target(orderId: 10, lineId: 100, maxAdditional: 5)]);
        var hu = session.FindHu("HU-A");
        Assert.True(session.StageBind(hu, session.FindTargetLine(100), out _));

        session.MarkSaveSuccess();

        Assert.Equal(10, hu!.OriginalOrderId);
        Assert.Equal(100, hu.OriginalOrderLineId);
        Assert.Equal(0, session.FindTargetLine(100)!.RemainingFutureCapacity);
        Assert.Equal(0, session.FindTargetLine(100)!.MaxAdditionalBindQty);
        Assert.Empty(session.Changes);
        Assert.Empty(session.BuildApplyRequest().Lines);
    }

    [Fact]
    public void HiddenReservation_SeedsFutureQtyAndReducesRemainingCapacity()
    {
        var session = CreateSession(
            hus: [],
            targets:
            [
                Target(
                    orderId: 10,
                    lineId: 100,
                    current: ["HU-HIDDEN"],
                    currentQty: 600,
                    maxAdditional: 200)
            ]);

        var line = session.FindTargetLine(100)!;
        Assert.Equal(600, line.FutureBoundQty, 3);
        Assert.Equal(200, line.RemainingFutureCapacity, 3);
        Assert.Equal(["HU-HIDDEN"], line.FutureBoundHuCodes);
    }

    [Fact]
    public void HiddenReservation_BlocksBindAboveRealCapacity()
    {
        var session = CreateSession(
            hus: [Hu("HU-FREE", qty: 300)],
            targets:
            [
                Target(
                    orderId: 10,
                    lineId: 100,
                    current: ["HU-HIDDEN"],
                    currentQty: 600,
                    maxAdditional: 200)
            ]);

        Assert.False(session.StageBind(session.FindHu("HU-FREE"), session.FindTargetLine(100), out var message));
        Assert.Contains("превышает", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(600, session.FindTargetLine(100)!.FutureBoundQty, 3);
    }

    [Fact]
    public void DetachVisibleHu_PreservesHiddenCodesAndSubtractsOnlyVisibleQty()
    {
        var session = CreateSession(
            hus: [Hu("HU-VISIBLE", qty: 200, assignment: Assignment(10, 100))],
            targets:
            [
                Target(
                    orderId: 10,
                    lineId: 100,
                    current: ["HU-HIDDEN-1", "HU-VISIBLE", "HU-HIDDEN-2"],
                    currentQty: 1000)
            ]);

        Assert.True(session.StageDetach(session.FindHu("HU-VISIBLE"), out var message), message);

        var target = session.FindTargetLine(100)!;
        Assert.Equal(800, target.FutureBoundQty, 3);
        Assert.Equal(["HU-HIDDEN-1", "HU-HIDDEN-2"], target.FutureBoundHuCodes);
        var requestLine = Assert.Single(session.BuildApplyRequest().Lines);
        Assert.Equal(["HU-HIDDEN-1", "HU-HIDDEN-2", "HU-VISIBLE"], requestLine.ExpectedBoundHuCodes);
        Assert.Equal(["HU-HIDDEN-1", "HU-HIDDEN-2"], requestLine.FinalHuCodes);
    }

    [Fact]
    public void MoveVisibleHu_UpdatesFullFutureQtyForBothLines()
    {
        var session = CreateSession(
            hus: [Hu("HU-MOVE", qty: 200, assignment: Assignment(10, 100))],
            targets:
            [
                Target(10, 100, current: ["HU-MOVE", "HU-SOURCE-HIDDEN"], currentQty: 800),
                Target(20, 200, current: ["HU-TARGET-HIDDEN"], currentQty: 600, maxAdditional: 200)
            ]);

        Assert.True(session.StageBind(session.FindHu("HU-MOVE"), session.FindTargetLine(200), out var message), message);

        Assert.Equal(600, session.FindTargetLine(100)!.FutureBoundQty, 3);
        Assert.Equal(["HU-SOURCE-HIDDEN"], session.FindTargetLine(100)!.FutureBoundHuCodes);
        Assert.Equal(800, session.FindTargetLine(200)!.FutureBoundQty, 3);
        Assert.Equal(["HU-MOVE", "HU-TARGET-HIDDEN"], session.FindTargetLine(200)!.FutureBoundHuCodes);
    }

    [Fact]
    public void RefreshProjection_DoesNotChangeFullFutureState()
    {
        var targets = new[]
        {
            Target(10, 100, current: ["HU-HIDDEN"], currentQty: 600, maxAdditional: 200)
        };
        var session = CreateSession(hus: [Hu("HU-PAGE-1", qty: 100)], targets: targets);

        Assert.True(session.TryRefreshFrom(
            Page(Hu("HU-PAGE-2", qty: 100)),
            targets,
            discardStagedChanges: true,
            out var message), message);

        var line = session.FindTargetLine(100)!;
        Assert.Equal(600, line.FutureBoundQty, 3);
        Assert.Equal(200, line.RemainingFutureCapacity, 3);
        Assert.Equal(["HU-HIDDEN"], line.FutureBoundHuCodes);
        Assert.Empty(session.BuildApplyRequest().Lines);
    }

    [Fact]
    public void MarkSaveSuccess_PreservesHiddenSnapshotAndCommitsFullQuantity()
    {
        var session = CreateSession(
            hus: [Hu("HU-NEW", qty: 100)],
            targets:
            [
                Target(10, 100, current: ["HU-HIDDEN"], currentQty: 600, maxAdditional: 100)
            ]);
        Assert.True(session.StageBind(session.FindHu("HU-NEW"), session.FindTargetLine(100), out _));

        session.MarkSaveSuccess();

        var line = session.FindTargetLine(100)!;
        Assert.Equal(["HU-HIDDEN", "HU-NEW"], line.OriginalBoundHuCodes);
        Assert.Equal(["HU-HIDDEN", "HU-NEW"], line.FutureBoundHuCodes);
        Assert.Equal(700, line.CurrentBoundQty, 3);
        Assert.Equal(700, line.FutureBoundQty, 3);
        Assert.Empty(session.BuildApplyRequest().Lines);
    }

    [Fact]
    public void ExistingVisibleHuMissingFromFullSnapshot_FailsClosed()
    {
        var session = CreateSession(
            hus: [Hu("HU-STALE", qty: 100, assignment: Assignment(10, 100))],
            targets: [Target(10, 100, current: ["HU-OTHER"], currentQty: 100)]);

        Assert.False(session.StageDetach(session.FindHu("HU-STALE"), out var message));
        Assert.Contains("полном snapshot", message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Changes);
    }

    private static HuAssignmentManagementSession CreateSession(
        IReadOnlyList<WpfHuBindingManageHuRow> hus,
        IReadOnlyList<WpfHuBindingManageTargetLine> targets,
        long itemId = 1) =>
        new(Page(itemId, $"Товар {itemId}", hus.ToArray()), targets);

    private static WpfHuBindingManageHuPage Page(params WpfHuBindingManageHuRow[] hus) =>
        Page(1, "Товар 1", hus);

    private static WpfHuBindingManageHuPage Page(long itemId, string itemName, params WpfHuBindingManageHuRow[] hus) =>
        new()
        {
            ItemId = itemId,
            ItemName = itemName,
            Total = hus.Length,
            Limit = 100,
            Offset = 0,
            HuRows = hus
        };

    private static WpfHuBindingManageHuRow Hu(
        string huCode,
        long itemId = 1,
        double qty = 1,
        WpfHuBindingManageHuAssignment? assignment = null) =>
        new()
        {
            HuCode = huCode,
            ItemId = itemId,
            ItemName = $"Товар {itemId}",
            Qty = qty,
            LocationDisplay = "A-1",
            State = assignment == null ? "FREE" : "BOUND",
            CurrentAssignment = assignment
        };

    private static WpfHuBindingManageHuAssignment Assignment(long orderId, long lineId) =>
        new()
        {
            OrderId = orderId,
            OrderRef = $"SO-{orderId:000}",
            PartnerName = $"Партнер {orderId}",
            OrderLineId = lineId,
            OrderStatus = "IN_PROGRESS",
            ReservedQty = 1
        };

    private static WpfHuBindingManageTargetLine Target(
        long orderId,
        long lineId,
        long itemId = 1,
        IReadOnlyList<string>? current = null,
        double currentQty = 0,
        double maxAdditional = 0) =>
        new()
        {
            OrderId = orderId,
            OrderRef = $"SO-{orderId:000}",
            PartnerName = $"Партнер {orderId}",
            OrderStatus = "IN_PROGRESS",
            DueAt = new DateTime(2026, 1, 1),
            OrderLineId = lineId,
            ItemId = itemId,
            QtyOrdered = currentQty + maxAdditional,
            QtyShipped = 0,
            CurrentBoundHuCodes = current ?? Array.Empty<string>(),
            CurrentBoundQty = currentQty,
            MaxAdditionalBindQty = maxAdditional
        };
}
