using FlowStock.App;

namespace FlowStock.Server.Tests.Orders;

public sealed class GlobalReadyHuBindingSessionTests
{
    [Fact]
    public void SelectHu_ExposesOnlyCompatibleOrdersAndLines()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");

        session.SelectHu(hu);

        Assert.All(
            session.CompatibleOrderGroups.SelectMany(group => group.Lines),
            line => Assert.Equal("HU-001", line.HuCode));
        Assert.Contains(session.CompatibleOrderGroups.SelectMany(group => group.Lines), line => line.OrderLineId == 1001);
        Assert.DoesNotContain(session.CompatibleOrderGroups.SelectMany(group => group.Lines), line => line.OrderLineId == 2001);
    }

    [Fact]
    public void StageBind_RemovesHuFromAvailableList()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");
        session.SelectHu(hu);
        var line = session.FindCompatibleLine(1001);

        Assert.True(session.StageBind(hu, line, out var message), message);

        Assert.Null(session.FindCandidate("HU-001"));
        Assert.Single(session.StagedBindings);
    }

    [Fact]
    public void StageDetach_ReturnsHuToAvailableList()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");
        session.SelectHu(hu);
        Assert.True(session.StageBind(hu, session.FindCompatibleLine(1001), out _));

        Assert.True(session.StageDetach(session.StagedBindings.Single(), out var message), message);

        Assert.NotNull(session.FindCandidate("HU-001"));
        Assert.Empty(session.StagedBindings);
    }

    [Fact]
    public void StageBind_BlocksDuplicateHu()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");
        session.SelectHu(hu);
        var line = session.FindCompatibleLine(1001);
        Assert.True(session.StageBind(hu, line, out _));

        Assert.False(session.StageBind(hu, line, out var message));

        Assert.Contains("недоступен", message);
        Assert.Single(session.StagedBindings);
    }

    [Fact]
    public void StageBind_BlocksItemMismatch()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");
        var wrongLine = new GlobalReadyHuCompatibleLineItem(
            "HU-001",
            BuildOrder(50, "050"),
            BuildLine(5001, itemId: 99, maxAdditional: 10));

        Assert.False(session.StageBind(hu, wrongLine, out var message));

        Assert.Contains("не соответствует", message);
        Assert.Empty(session.StagedBindings);
    }

    [Fact]
    public void StageBind_BlocksCapacityOverflow()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-002");
        var line = new GlobalReadyHuCompatibleLineItem(
            "HU-002",
            BuildOrder(10, "010"),
            BuildLine(1001, itemId: 1, maxAdditional: 6));

        Assert.False(session.StageBind(hu, line, out var message));

        Assert.Contains("превышает", message);
        Assert.Empty(session.StagedBindings);
    }

    [Fact]
    public void StageAuto_IsLocalOnlyAndRespectsFifoPriorityAndCapacity()
    {
        var session = new GlobalReadyHuBindingSession(BuildAutoReadModel());

        session.StageAuto();

        Assert.Collection(
            session.StagedBindings,
            binding => Assert.Equal("HU-A", binding.HuCode),
            binding => Assert.Equal("HU-B", binding.HuCode));
        Assert.Equal(20, session.StagedBindings[0].OrderId);
        Assert.Equal(10, session.StagedBindings[1].OrderId);
        Assert.DoesNotContain(session.StagedBindings, binding => binding.HuCode == "HU-C");
    }

    [Fact]
    public void BuildApplyFinalByOrder_BuildsExpectedAndFinalSets()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");
        session.SelectHu(hu);
        Assert.True(session.StageBind(hu, session.FindCompatibleLine(1001), out _));

        var batch = Assert.Single(session.BuildApplyFinalByOrder());
        var line = Assert.Single(batch.Lines);

        Assert.Equal(10, batch.OrderId);
        Assert.Equal(1001, line.OrderLineId);
        Assert.Equal(["SAVED-1"], line.ExpectedBoundHuCodes);
        Assert.Equal(["SAVED-1", "HU-001"], line.FinalHuCodes);
    }

    [Fact]
    public void BuildApplyFinalByOrder_DoesNotIncludePlannedHuCodes()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var hu = FindCandidate(session, "HU-001");
        session.SelectHu(hu);
        Assert.True(session.StageBind(hu, session.FindCompatibleLine(1001), out _));

        var line = Assert.Single(Assert.Single(session.BuildApplyFinalByOrder()).Lines);

        Assert.DoesNotContain("PLANNED-1", line.FinalHuCodes);
        Assert.Equal(["SAVED-1", "HU-001"], line.FinalHuCodes);
    }

    [Fact]
    public void MarkOrderApplySuccess_RemovesOnlySuccessfulOrderStagedEntries()
    {
        var session = new GlobalReadyHuBindingSession(BuildReadModel());
        var first = FindCandidate(session, "HU-001");
        session.SelectHu(first);
        Assert.True(session.StageBind(first, session.FindCompatibleLine(1001), out _));

        var second = FindCandidate(session, "HU-003");
        session.SelectHu(second);
        Assert.True(session.StageBind(second, session.FindCompatibleLine(2001), out _));

        session.MarkOrderApplySuccess(10);

        var remaining = Assert.Single(session.StagedBindings);
        Assert.Equal("HU-003", remaining.HuCode);
        Assert.Equal(20, remaining.OrderId);
    }

    private static GlobalReadyHuCandidateItem FindCandidate(GlobalReadyHuBindingSession session, string huCode) =>
        Assert.IsType<GlobalReadyHuCandidateItem>(
            session.CandidateGroups.SelectMany(group => group.Candidates)
                .First(candidate => candidate.HuCode == huCode));

    private static WpfReadyHuBindingReadModel BuildReadModel() =>
        new()
        {
            RequestType = "READY_HU_BINDING_AVAILABLE",
            HuCount = 3,
            OrderCount = 2,
            LineCount = 2,
            HuRows =
            [
                BuildHu("HU-001", itemId: 1, qty: 5, BuildOrder(10, "010", BuildLine(1001, itemId: 1, maxAdditional: 6, currentBoundHuCodes: ["SAVED-1"]))),
                BuildHu("HU-002", itemId: 1, qty: 7, BuildOrder(10, "010", BuildLine(1001, itemId: 1, maxAdditional: 6, currentBoundHuCodes: ["SAVED-1"]))),
                BuildHu("HU-003", itemId: 2, qty: 3, BuildOrder(20, "020", BuildLine(2001, itemId: 2, maxAdditional: 4)))
            ]
        };

    private static WpfReadyHuBindingReadModel BuildAutoReadModel() =>
        new()
        {
            RequestType = "READY_HU_BINDING_AVAILABLE",
            HuCount = 3,
            OrderCount = 2,
            LineCount = 2,
            HuRows =
            [
                BuildHu(
                    "HU-A",
                    itemId: 1,
                    qty: 4,
                    BuildOrder(10, "010", BuildLine(1001, itemId: 1, maxAdditional: 8), dueDate: new DateTime(2026, 2, 1), createdAt: new DateTime(2026, 1, 2)),
                    BuildOrder(20, "020", BuildLine(2001, itemId: 1, maxAdditional: 4), dueDate: new DateTime(2026, 1, 1), createdAt: new DateTime(2026, 1, 1))),
                BuildHu(
                    "HU-B",
                    itemId: 1,
                    qty: 7,
                    BuildOrder(10, "010", BuildLine(1001, itemId: 1, maxAdditional: 8), dueDate: new DateTime(2026, 2, 1), createdAt: new DateTime(2026, 1, 2))),
                BuildHu(
                    "HU-C",
                    itemId: 1,
                    qty: 99,
                    BuildOrder(30, "030", BuildLine(3001, itemId: 1, maxAdditional: 5), createdAt: new DateTime(2026, 1, 3)))
            ]
        };

    private static WpfReadyHuBindingHuRow BuildHu(
        string huCode,
        long itemId,
        double qty,
        params WpfReadyHuBindingCompatibleOrderRow[] orders) =>
        new()
        {
            HuCode = huCode,
            ItemId = itemId,
            ItemName = $"Item {itemId}",
            Qty = qty,
            Source = "LEDGER_STOCK",
            LocationDisplay = "MAIN",
            CompatibleOrders = orders
        };

    private static WpfReadyHuBindingCompatibleOrderRow BuildOrder(
        long orderId,
        string orderRef,
        params WpfReadyHuBindingCompatibleLineRow[] lines) =>
        BuildOrder(orderId, orderRef, lines, null, new DateTime(2026, 1, 1));

    private static WpfReadyHuBindingCompatibleOrderRow BuildOrder(
        long orderId,
        string orderRef,
        WpfReadyHuBindingCompatibleLineRow line,
        DateTime? dueDate = null,
        DateTime? createdAt = null) =>
        BuildOrder(orderId, orderRef, [line], dueDate, createdAt ?? new DateTime(2026, 1, 1));

    private static WpfReadyHuBindingCompatibleOrderRow BuildOrder(
        long orderId,
        string orderRef,
        IReadOnlyList<WpfReadyHuBindingCompatibleLineRow> lines,
        DateTime? dueDate,
        DateTime createdAt) =>
        new()
        {
            OrderId = orderId,
            OrderRef = orderRef,
            PartnerName = $"Client {orderRef}",
            Status = "IN_PROGRESS",
            DueDate = dueDate,
            CreatedAt = createdAt,
            Lines = lines
        };

    private static WpfReadyHuBindingCompatibleLineRow BuildLine(
        long lineId,
        long itemId,
        double maxAdditional,
        IReadOnlyList<string>? currentBoundHuCodes = null) =>
        new()
        {
            OrderLineId = lineId,
            ItemId = itemId,
            ItemName = $"Item {itemId}",
            QtyOrdered = 10,
            QtyShipped = 0,
            ShipmentRemainingQty = 10,
            CurrentBoundHuCodes = currentBoundHuCodes ?? Array.Empty<string>(),
            CurrentBoundQty = currentBoundHuCodes?.Count ?? 0,
            MaxAdditionalBindQty = maxAdditional
        };
}
