using FlowStock.App;

namespace FlowStock.Server.Tests.Orders;

public sealed class HuAssignmentManagementControllerTests
{
    [Fact]
    public void LoadInitial_LoadsItemsHuTargetsAndCreatesSession()
    {
        var api = new FakeManageApi();
        var controller = new HuAssignmentManagementController(api);

        Assert.True(controller.LoadInitial(out var message), message);

        Assert.Single(controller.Items);
        Assert.NotNull(controller.Session);
        Assert.Single(controller.Session!.HuRows);
        Assert.Single(controller.Session.TargetLines);
        Assert.Equal(1, api.GetItemsCalls);
        Assert.Equal(1, api.GetHusCalls);
        Assert.Equal(1, api.GetTargetsCalls);
    }

    [Fact]
    public void BindDetachMove_AreLocalOnlyUntilSave()
    {
        var api = new FakeManageApi(
            hus:
            [
                Hu("HU-FREE", qty: 5),
                Hu("HU-BOUND", qty: 5, assignment: Assignment(10, 100))
            ],
            targets:
            [
                Target(10, 100, current: ["HU-BOUND"], currentQty: 5, maxAdditional: 5),
                Target(20, 200, maxAdditional: 10)
            ]);
        var controller = Loaded(api);

        controller.SelectHu(controller.Session!.FindHu("HU-FREE"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out var message), message);

        controller.SelectHu(controller.Session.FindHu("HU-FREE"));
        Assert.True(controller.DetachSelected(out message), message);

        controller.SelectHu(controller.Session.FindHu("HU-BOUND"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(200));
        Assert.True(controller.MoveSelected(out message), message);

        Assert.Equal(0, api.ApplyCalls);
        Assert.Single(controller.Session.Changes);
        Assert.Equal("Будет перенесён", controller.Session.FindHu("HU-BOUND")!.ChangeDisplay);
    }

    [Fact]
    public void Save_BuildsOneBatchAndReloadsCanonicalData()
    {
        var api = new FakeManageApi(hus: [Hu("HU-FREE", qty: 5)], targets: [Target(10, 100, maxAdditional: 5)]);
        var controller = Loaded(api);
        controller.SelectHu(controller.Session!.FindHu("HU-FREE"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out _));

        var result = controller.Save();

        Assert.Equal(HuAssignmentManagementSaveOutcome.Success, result.Outcome);
        Assert.Equal(1, api.ApplyCalls);
        var line = Assert.Single(api.LastApplyRequest!.Lines);
        Assert.Equal(100, line.OrderLineId);
        Assert.Equal(["HU-FREE"], line.FinalHuCodes);
        Assert.Equal(2, api.GetHusCalls);
        Assert.Empty(controller.Session!.Changes);
    }

    [Fact]
    public void SaveSuccess_CommitsBeforeCanonicalReload()
    {
        var api = new FakeManageApi(hus: [Hu("HU-FREE", qty: 5)], targets: [Target(10, 100, maxAdditional: 5)]);
        var controller = Loaded(api);
        var hu = controller.Session!.FindHu("HU-FREE");
        controller.SelectHu(hu);
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out _));

        _ = controller.Save();

        Assert.NotNull(api.LastApplyRequest);
        Assert.Empty(controller.Session!.Changes);
    }

    [Fact]
    public void StaleSave_ReloadsCanonicalDataAndDiscardsLocalFutureState()
    {
        var api = new FakeManageApi(hus: [Hu("HU-FREE", qty: 5)], targets: [Target(10, 100, maxAdditional: 5)])
        {
            ApplySucceeds = false,
            ApplyError = new WpfHuBindingManageApplyError { ErrorCode = "HU_OWNER_CHANGED", Message = "changed" }
        };
        var controller = Loaded(api);
        controller.SelectHu(controller.Session!.FindHu("HU-FREE"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out _));

        var result = controller.Save();

        Assert.Equal(HuAssignmentManagementSaveOutcome.StaleReloaded, result.Outcome);
        Assert.Equal(2, api.GetHusCalls);
        Assert.Empty(controller.Session!.Changes);
        Assert.Null(controller.Session.FindHu("HU-FREE")!.FutureOrderLineId);
    }

    [Fact]
    public void NetworkFailure_PreservesLocalFutureState()
    {
        var api = new FakeManageApi(hus: [Hu("HU-FREE", qty: 5)], targets: [Target(10, 100, maxAdditional: 5)])
        {
            ApplySucceeds = false,
            ApplyError = null
        };
        var controller = Loaded(api);
        controller.SelectHu(controller.Session!.FindHu("HU-FREE"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out _));

        var result = controller.Save();

        Assert.Equal(HuAssignmentManagementSaveOutcome.NetworkFailure, result.Outcome);
        Assert.Equal(1, api.GetHusCalls);
        Assert.Single(controller.Session!.Changes);
        Assert.Equal(100, controller.Session.FindHu("HU-FREE")!.FutureOrderLineId);
    }

    [Fact]
    public void MixedHuAndCapacityOverflow_DisableBindCommand()
    {
        var api = new FakeManageApi(
            hus:
            [
                Hu("HU-MIX", qty: 1, isMixed: true),
                Hu("HU-BIG", qty: 10)
            ],
            targets: [Target(10, 100, maxAdditional: 5)]);
        var controller = Loaded(api);

        controller.SelectHu(controller.Session!.FindHu("HU-MIX"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.False(controller.CanBind);

        controller.SelectHu(controller.Session.FindHu("HU-BIG"));
        Assert.False(controller.CanBind);
    }

    [Fact]
    public void Pagination_IsBlockedWhenUnsavedChangesExist()
    {
        var api = new FakeManageApi(hus: [Hu("HU-FREE", qty: 5)], targets: [Target(10, 100, maxAdditional: 5)])
        {
            Total = 200
        };
        var controller = Loaded(api);
        controller.SelectHu(controller.Session!.FindHu("HU-FREE"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out _));

        Assert.False(controller.NextPage(out var message));

        Assert.Contains("сохраните", message);
        Assert.Equal(1, api.GetHusCalls);
        Assert.Single(controller.Session.Changes);
    }

    [Fact]
    public void ResetAllChanges_ReturnsSessionToOriginalState()
    {
        var api = new FakeManageApi(hus: [Hu("HU-FREE", qty: 5)], targets: [Target(10, 100, maxAdditional: 5)]);
        var controller = Loaded(api);
        controller.SelectHu(controller.Session!.FindHu("HU-FREE"));
        controller.SelectTargetLine(controller.Session.FindTargetLine(100));
        Assert.True(controller.BindSelected(out _));

        controller.ResetAllChanges();

        Assert.Empty(controller.Session!.Changes);
        Assert.Null(controller.Session.FindHu("HU-FREE")!.FutureOrderLineId);
    }

    private static HuAssignmentManagementController Loaded(FakeManageApi api)
    {
        var controller = new HuAssignmentManagementController(api);
        Assert.True(controller.LoadInitial(out var message), message);
        return controller;
    }

    private static WpfHuBindingManageHuRow Hu(
        string huCode,
        long itemId = 1,
        double qty = 1,
        bool isMixed = false,
        WpfHuBindingManageHuAssignment? assignment = null) =>
        new()
        {
            HuCode = huCode,
            ItemId = itemId,
            ItemName = $"Товар {itemId}",
            Qty = qty,
            LocationDisplay = "A-1",
            State = assignment == null ? "FREE" : "BOUND",
            IsMixed = isMixed,
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
            ItemId = 1,
            QtyOrdered = currentQty + maxAdditional,
            QtyShipped = 0,
            CurrentBoundHuCodes = current ?? Array.Empty<string>(),
            CurrentBoundQty = currentQty,
            MaxAdditionalBindQty = maxAdditional
        };

    private sealed class FakeManageApi : IHuAssignmentManagementApiClient
    {
        private readonly IReadOnlyList<WpfHuBindingManageHuRow> _hus;
        private readonly IReadOnlyList<WpfHuBindingManageTargetLine> _targets;

        public FakeManageApi(
            IReadOnlyList<WpfHuBindingManageHuRow>? hus = null,
            IReadOnlyList<WpfHuBindingManageTargetLine>? targets = null)
        {
            _hus = hus ?? [Hu("HU-001", qty: 5)];
            _targets = targets ?? [Target(10, 100, maxAdditional: 5)];
        }

        public int Total { get; init; }
        public bool ApplySucceeds { get; init; } = true;
        public WpfHuBindingManageApplyError? ApplyError { get; init; }
        public int GetItemsCalls { get; private set; }
        public int GetHusCalls { get; private set; }
        public int GetTargetsCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public WpfHuBindingManageApplyRequest? LastApplyRequest { get; private set; }

        public bool TryGetManageItems(string? search, int limit, out IReadOnlyList<WpfHuBindingManageItemRow> items)
        {
            GetItemsCalls++;
            items = [new WpfHuBindingManageItemRow { ItemId = 1, ItemName = "Товар 1", HuCount = _hus.Count }];
            return true;
        }

        public bool TryGetManageHus(long itemId, WpfHuBindingManageHuFilter filter, out WpfHuBindingManageHuPage page)
        {
            GetHusCalls++;
            page = new WpfHuBindingManageHuPage
            {
                ItemId = itemId,
                ItemName = $"Товар {itemId}",
                Total = Total == 0 ? _hus.Count : Total,
                Limit = filter.Limit,
                Offset = filter.Offset,
                HuRows = _hus
            };
            return true;
        }

        public bool TryGetManageTargets(long itemId, out IReadOnlyList<WpfHuBindingManageTargetLine> targets)
        {
            GetTargetsCalls++;
            targets = _targets;
            return true;
        }

        public bool TryApplyManageHuBindings(
            WpfHuBindingManageApplyRequest request,
            out WpfHuBindingManageApplyResult? result,
            out WpfHuBindingManageApplyError? error)
        {
            ApplyCalls++;
            LastApplyRequest = request;
            error = ApplyError;
            result = ApplySucceeds
                ? new WpfHuBindingManageApplyResult { Ok = true }
                : null;
            return ApplySucceeds;
        }
    }
}
