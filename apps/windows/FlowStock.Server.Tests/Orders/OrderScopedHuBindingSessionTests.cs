using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderScopedHuBindingSessionTests
{
    [Fact]
    public void BuildCandidatesRequestLines_UsesOnlyCurrentOrderLines()
    {
        var session = CreateSession(
            lines:
            [
                Line(101, itemId: 6, qty: 600),
                Line(102, itemId: 7, qty: 300)
            ]);

        var requestLines = session.BuildCandidatesRequestLines();

        Assert.Equal(new List<long> { 101L, 102L }, requestLines.Select(line => line.OrderLineId!.Value).ToList());
        Assert.All(requestLines, line => Assert.StartsWith("line-", line.ClientLineKey, StringComparison.Ordinal));
    }

    [Fact]
    public void StageBind_MovesHuIntoFutureState()
    {
        var session = CreateSession(lines: [Line(101, itemId: 6, qty: 600)]);
        session.ApplyCandidates(Candidates(101, itemId: 6, Candidate("HU-1", 6, 600)));

        var candidate = SingleCandidate(session);
        var line = Assert.Single(session.Lines);
        var ok = session.StageBind(candidate, line, out var message);

        Assert.True(ok, message);
        Assert.Equal(["HU-1"], line.FutureHuCodes);
        Assert.Empty(session.CandidateGroups.SelectMany(group => group.Candidates));
    }

    [Fact]
    public void StageDetach_RemovesSavedHuAndBuildsExplicitDetach()
    {
        var session = CreateSession(
            lines: [Line(101, itemId: 6, qty: 600)],
            saved: [Plan(101, itemId: 6, "HU-OLD", 600)]);
        var line = Assert.Single(session.Lines);
        var savedHu = Assert.Single(line.FutureHu);

        var ok = session.StageDetach(savedHu, out var message);
        var request = Assert.Single(session.BuildApplyFinalLines());

        Assert.True(ok, message);
        Assert.Empty(line.FutureHuCodes);
        Assert.Equal(["HU-OLD"], request.ExpectedBoundHuCodes);
        Assert.Empty(request.FinalHuCodes);
    }

    [Fact]
    public void StageDetach_ForStagedHuCancelsStagedBind()
    {
        var session = CreateSession(lines: [Line(101, itemId: 6, qty: 600)]);
        session.ApplyCandidates(Candidates(101, itemId: 6, Candidate("HU-1", 6, 600)));
        var line = Assert.Single(session.Lines);
        Assert.True(session.StageBind(SingleCandidate(session), line, out _));

        Assert.True(session.StageDetach(Assert.Single(line.FutureHu), out _));

        Assert.Empty(line.FutureHuCodes);
        Assert.Empty(session.BuildApplyFinalLines());
        Assert.Equal("HU-1", SingleCandidate(session).HuCode);
    }

    [Fact]
    public void StageAuto_StagesFifoByItemWithoutPersisting()
    {
        var session = CreateSession(
            lines:
            [
                Line(101, itemId: 6, qty: 1200),
                Line(102, itemId: 7, qty: 300)
            ]);
        session.ApplyCandidates(new WpfHuReservationCandidatesResult
        {
            Lines =
            [
                CandidateLine(101, 6, Candidate("HU-B", 6, 600), Candidate("HU-A", 6, 600)),
                CandidateLine(102, 7, Candidate("HU-X", 7, 300))
            ]
        });

        session.StageAuto();

        Assert.Equal(["HU-B", "HU-A"], session.Lines[0].FutureHuCodes);
        Assert.Equal(["HU-X"], session.Lines[1].FutureHuCodes);
        Assert.Equal(2, session.BuildApplyFinalLines().Count);
    }

    [Fact]
    public void BuildApplyFinalLines_SendsExpectedAndFinalForAffectedLinesOnly()
    {
        var session = CreateSession(
            lines:
            [
                Line(101, itemId: 6, qty: 1200),
                Line(102, itemId: 7, qty: 300)
            ],
            saved:
            [
                Plan(101, itemId: 6, "HU-OLD", 600),
                Plan(102, itemId: 7, "HU-KEEP", 300)
            ]);
        session.ApplyCandidates(Candidates(101, itemId: 6, Candidate("HU-NEW", 6, 600)));
        Assert.True(session.StageBind(SingleCandidate(session), session.Lines[0], out _));

        var request = Assert.Single(session.BuildApplyFinalLines());

        Assert.Equal(101, request.OrderLineId);
        Assert.Equal(["HU-OLD"], request.ExpectedBoundHuCodes);
        Assert.Equal(["HU-OLD", "HU-NEW"], request.FinalHuCodes);
    }

    [Fact]
    public void CapturedExpandedState_SurvivesStageBindDetachAndAuto()
    {
        var session = CreateSession(
            lines:
            [
                Line(101, itemId: 6, qty: 1200),
                Line(102, itemId: 7, qty: 300)
            ],
            saved: [Plan(101, itemId: 6, "HU-OLD", 600)]);
        session.ApplyCandidates(new WpfHuReservationCandidatesResult
        {
            Lines =
            [
                CandidateLine(101, 6, Candidate("HU-NEW", 6, 600)),
                CandidateLine(102, 7, Candidate("HU-X", 7, 300))
            ]
        });
        session.CaptureUiState(
            [ReadyHuBindingCandidateGroup.BuildKey(6)],
            [ReadyHuBindingOrderGroup.RootKey, ReadyHuBindingLineItem.BuildKey(101)],
            selectedCandidateHuCode: "HU-NEW",
            selectedLineId: 101,
            selectedHuCode: "HU-OLD");

        Assert.True(session.StageBind(session.FindCandidate("HU-NEW"), session.FindLine(101), out _));
        Assert.Contains(ReadyHuBindingCandidateGroup.BuildKey(6), session.ExpandedCandidateGroupKeys);
        Assert.Contains(ReadyHuBindingOrderGroup.RootKey, session.ExpandedOrderGroupKeys);
        Assert.Contains(ReadyHuBindingLineItem.BuildKey(101), session.ExpandedOrderGroupKeys);
        Assert.Equal("HU-NEW", session.SelectedCandidateHuCode);
        Assert.Equal(101, session.SelectedLineId);

        session.StageAuto();
        Assert.Contains(ReadyHuBindingCandidateGroup.BuildKey(6), session.ExpandedCandidateGroupKeys);
        Assert.Contains(ReadyHuBindingLineItem.BuildKey(101), session.ExpandedOrderGroupKeys);

        Assert.True(session.StageDetach(session.FindFutureHu("HU-OLD"), out _));
        Assert.Contains(ReadyHuBindingCandidateGroup.BuildKey(6), session.ExpandedCandidateGroupKeys);
        Assert.Contains(ReadyHuBindingLineItem.BuildKey(101), session.ExpandedOrderGroupKeys);
    }

    private static OrderScopedHuBindingSession CreateSession(
        IReadOnlyList<OrderLineView> lines,
        IReadOnlyList<OrderReceiptPlanLine>? saved = null) =>
        new(
            new Order
            {
                Id = 10,
                OrderRef = "SO-010",
                Type = OrderType.Customer,
                Status = OrderStatus.InProgress
            },
            lines,
            saved ?? Array.Empty<OrderReceiptPlanLine>());

    private static OrderLineView Line(long id, long itemId, double qty) =>
        new()
        {
            Id = id,
            OrderId = 10,
            ItemId = itemId,
            ItemName = $"Товар {itemId}",
            QtyOrdered = qty,
            QtyShipped = 0
        };

    private static OrderReceiptPlanLine Plan(long orderLineId, long itemId, string huCode, double qty) =>
        new()
        {
            OrderId = 10,
            OrderLineId = orderLineId,
            ItemId = itemId,
            QtyPlanned = qty,
            ToHu = huCode
        };

    private static WpfHuReservationCandidatesResult Candidates(long orderLineId, long itemId, params WpfHuReservationCandidateRow[] candidates) =>
        new()
        {
            Lines = [CandidateLine(orderLineId, itemId, candidates)]
        };

    private static WpfHuReservationCandidatesLineResult CandidateLine(long orderLineId, long itemId, params WpfHuReservationCandidateRow[] candidates) =>
        new()
        {
            ClientLineKey = $"line-{orderLineId}",
            OrderLineId = orderLineId,
            ItemId = itemId,
            QtyOrdered = candidates.Sum(candidate => candidate.Qty),
            Candidates = candidates
        };

    private static WpfHuReservationCandidateRow Candidate(string huCode, long itemId, double qty) =>
        new()
        {
            HuCode = huCode,
            Source = "LEDGER_STOCK",
            Qty = qty,
            ShipReady = true
        };

    private static ReadyHuBindingCandidateItem SingleCandidate(OrderScopedHuBindingSession session) =>
        Assert.Single(session.CandidateGroups.SelectMany(group => group.Candidates));
}
