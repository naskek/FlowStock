using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.HuOperator;

public sealed class HuMutationEligibilityPolicyTests
{
    [Fact]
    public void LockMutationScope_StoreWithoutTransactionalHuCapabilities_FailsClosed()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict).Object;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            HuMutationEligibilityService.LockMutationScope(store, [77], ["HU-1"]));

        Assert.Contains("transaction-scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutboundClose_ExactSingleHu_IsAllowed()
    {
        var decision = HuMutationEligibilityPolicy.Evaluate(
            new HuOperatorFacts
            {
                HuCode = "HU-1",
                Stock =
                [
                    new HuOperatorStockFact
                    {
                        ItemId = 10,
                        LocationId = 5,
                        LocationCode = "MAIN",
                        Qty = 100
                    }
                ]
            },
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                TargetOrderId = 77,
                CurrentOutboundDocumentId = 91,
                RequestedComponents =
                [
                    new HuMutationRequestedComponent(10, 100, 5)
                ]
            });

        Assert.True(decision.Allowed);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void OutboundClose_PartialSingleHu_IsRejected()
    {
        var decision = HuMutationEligibilityPolicy.Evaluate(
            StockFacts("HU-1", (10, 100d, 5L)),
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                RequestedComponents = [new HuMutationRequestedComponent(10, 40, 5)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuPartialQuantityNotAllowed);
    }

    [Fact]
    public void OutboundClose_HuInAnotherDraft_IsRejectedButCurrentDocumentIsIgnored()
    {
        var facts = StockFacts("HU-1", (10, 100d, 5L));
        facts = new HuOperatorFacts
        {
            HuCode = facts.HuCode,
            Stock = facts.Stock,
            Outbound =
            [
                DraftOutbound(91),
                DraftOutbound(92)
            ]
        };

        var decision = HuMutationEligibilityPolicy.Evaluate(
            facts,
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                CurrentOutboundDocumentId = 91,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100, 5)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuInOtherOutbound);
    }

    [Fact]
    public void Reservation_MixedHu_IsRejectedByItemScopedCommand()
    {
        var decision = HuMutationEligibilityPolicy.Evaluate(
            StockFacts("HU-MIX", (10, 100d, 5L), (20, 50d, 5L)),
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.ReserveOrBind,
                TargetOrderId = 77,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100, 5)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuMixedNotSupported);
    }

    [Fact]
    public void ReleaseProducedStock_CompleteFilledProductionHuWithoutLedger_IsAllowed()
    {
        var decision = HuMutationEligibilityPolicy.Evaluate(
            new HuOperatorFacts
            {
                HuCode = "HU-RELEASE",
                ProductionPallets =
                [
                    new HuOperatorProductionPalletFact
                    {
                        PalletId = 10,
                        Status = ProductionPalletStatus.Filled,
                        OwnerOrderId = 77,
                        Components =
                        [
                            new HuOperatorComponentFact
                            {
                                OrderLineId = 701,
                                OrderLineOrderId = 77,
                                ItemId = 10,
                                PlannedQty = 100,
                                FilledQty = 100
                            }
                        ]
                    }
                ]
            },
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.ReleaseProducedStock,
                SourceOrderId = 77,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100)]
            });

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void OutboundClose_CanonicalInconsistentFacts_AreRejected()
    {
        var facts = StockFacts("HU-BAD", (10, 60d, 5L));
        facts = new HuOperatorFacts
        {
            HuCode = facts.HuCode,
            Stock = facts.Stock,
            Outbound =
            [
                new HuOperatorOutboundFact
                {
                    DocumentId = 80,
                    DocumentRef = "OUT-80",
                    DocumentStatus = "CLOSED",
                    ItemId = 10,
                    Qty = 40
                }
            ],
            LedgerMovements =
            [
                Movement(1, 70, "INBOUND", 100),
                Movement(2, 80, "OUTBOUND", -40)
            ]
        };

        var decision = HuMutationEligibilityPolicy.Evaluate(
            facts,
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                CurrentOutboundDocumentId = 91,
                RequestedComponents = [new HuMutationRequestedComponent(10, 60, 5)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuInconsistent);
    }

    [Fact]
    public void OutboundClose_ReservationForAnotherOrder_IsRejected()
    {
        var facts = StockFacts("HU-RESERVED", (10, 100d, 5L));
        facts = new HuOperatorFacts
        {
            HuCode = facts.HuCode,
            Stock = facts.Stock,
            Reservations =
            [
                new HuOperatorReservationFact
                {
                    OrderId = 88,
                    OrderRef = "ORD-88",
                    OrderType = "CUSTOMER",
                    OrderStatus = "ACCEPTED",
                    ItemId = 10,
                    Qty = 100
                }
            ]
        };

        var decision = HuMutationEligibilityPolicy.Evaluate(
            facts,
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                TargetOrderId = 77,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100, 5)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuReservedByOtherOrder);
    }

    [Fact]
    public void OutboundClose_ExplicitDifferentLocation_IsRejectedAsCompositionMismatch()
    {
        var facts = StockFacts("HU-LOC", (10, 100d, 5L));

        var decision = HuMutationEligibilityPolicy.Evaluate(
            facts,
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100, 999)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuCompositionMismatch);
    }

    [Fact]
    public void OutboundClose_MultiplePositiveLocationsWithoutRequestedLocation_IsRejectedExplicitly()
    {
        var facts = StockFacts("HU-MULTI-LOC", (10, 60d, 5L), (10, 40d, 6L));

        var decision = HuMutationEligibilityPolicy.Evaluate(
            facts,
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.OutboundClose,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuMultipleLocations);
    }

    private static HuOperatorFacts StockFacts(
        string huCode,
        params (long ItemId, double Qty, long LocationId)[] components) => new()
    {
        HuCode = huCode,
        Stock = components
            .Select(component => new HuOperatorStockFact
            {
                ItemId = component.ItemId,
                LocationId = component.LocationId,
                LocationCode = $"LOC-{component.LocationId}",
                Qty = component.Qty
            })
            .ToArray()
    };

    private static HuOperatorOutboundFact DraftOutbound(long documentId) => new()
    {
        DocumentId = documentId,
        DocumentRef = $"OUT-{documentId}",
        DocumentStatus = "DRAFT",
        ItemId = 10,
        Qty = 100
    };

    private static HuOperatorLedgerMovementFact Movement(
        long ledgerId,
        long documentId,
        string documentType,
        double qtyDelta) => new()
    {
        LedgerId = ledgerId,
        Timestamp = new DateTime(2026, 1, 1).AddMinutes(ledgerId),
        DocumentId = documentId,
        DocumentRef = $"DOC-{documentId}",
        DocumentType = documentType,
        DocumentStatus = "CLOSED",
        ItemId = 10,
        LocationId = 5,
        LocationCode = "MAIN",
        QtyDelta = qtyDelta
    };
}
