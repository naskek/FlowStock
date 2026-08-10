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
    public void Reservation_WholeShippedHuWithMatchingHistoricalReservation_IsRejectedWithoutStock()
    {
        var decision = HuMutationEligibilityPolicy.Evaluate(
            new HuOperatorFacts
            {
                HuCode = "HU-SHIPPED-HISTORY",
                Reservations =
                [
                    new HuOperatorReservationFact
                    {
                        OrderId = 77,
                        OrderRef = "077",
                        OrderType = "CUSTOMER",
                        OrderStatus = "ACCEPTED",
                        OrderLineId = 701,
                        ItemId = 10,
                        Qty = 100
                    }
                ],
                Outbound =
                [
                    new HuOperatorOutboundFact
                    {
                        DocumentId = 91,
                        DocumentRef = "OUT-91",
                        DocumentStatus = "CLOSED",
                        OrderId = 77,
                        OrderRef = "077",
                        OrderType = "CUSTOMER",
                        OrderLineId = 701,
                        ItemId = 10,
                        Qty = 100
                    }
                ],
                LedgerMovements =
                [
                    Movement(1, 10, "PRODUCTION_RECEIPT", 100),
                    Movement(2, 91, "OUTBOUND", -100)
                ]
            },
            new HuMutationEligibilityContext
            {
                Operation = HuMutationOperation.ReserveOrBind,
                TargetOrderId = 77,
                RequestedComponents = [new HuMutationRequestedComponent(10, 100, 5)]
            });

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuNotInPhysicalStock);
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
                        OwnerOrderType = "CUSTOMER",
                        OwnerOrderStatus = "IN_PROGRESS",
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
    public void ReleaseProducedStock_NonFilledPalletWithComponentProgress_FailsClosed()
    {
        var decision = HuMutationEligibilityPolicy.Evaluate(
            new HuOperatorFacts
            {
                HuCode = "HU-STATUS-FILL-MISMATCH",
                ProductionPallets =
                [
                    new HuOperatorProductionPalletFact
                    {
                        PalletId = 10,
                        Status = ProductionPalletStatus.Planned,
                        OwnerOrderId = 77,
                        OwnerOrderType = "CUSTOMER",
                        OwnerOrderStatus = "IN_PROGRESS",
                        Components =
                        [
                            new HuOperatorComponentFact
                            {
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

        Assert.False(decision.Allowed);
        Assert.Contains(
            decision.Reasons,
            reason => reason.Code == HuMutationEligibilityReasonCode.HuInconsistent);
    }

    [Fact]
    public void ReleaseProducedStock_RecoveryWithAnyAdditionalConflict_FailsClosed()
    {
        var secondPallet = BuildRecoveryPallet(palletId: 11);
        var cases = new (string Name, HuOperatorFacts Facts, HuMutationEligibilityContext Context)[]
        {
            ("positive balance", RecoveryFacts(stock:
                [new HuOperatorStockFact { ItemId = 10, LocationId = 5, Qty = 1 }]), RecoveryContext()),
            ("negative balance", RecoveryFacts(stock:
                [new HuOperatorStockFact { ItemId = 10, LocationId = 5, Qty = -1 }]), RecoveryContext()),
            ("multiple active pallets", RecoveryFacts(pallets: [BuildRecoveryPallet(), secondPallet]), RecoveryContext()),
            ("ambiguous ownership", RecoveryFacts(componentOwnerOrderId: null), RecoveryContext()),
            ("non-customer ownership", RecoveryFacts(ownerOrderType: "INTERNAL"), RecoveryContext()),
            ("incomplete composition", RecoveryFacts(filledQty: 99), RecoveryContext()),
            ("requested composition mismatch", RecoveryFacts(), RecoveryContext(requestedQty: 99)),
            ("foreign reservation", RecoveryFacts(reservations:
                [new HuOperatorReservationFact
                {
                    OrderId = 88,
                    OrderRef = "ORD-88",
                    OrderType = "CUSTOMER",
                    OrderStatus = "ACCEPTED",
                    ItemId = 10,
                    Qty = 100
                }]), RecoveryContext()),
            ("other active draft outbound", RecoveryFacts(outbound:
                [new HuOperatorOutboundFact
                {
                    DocumentId = 91,
                    DocumentRef = "OUT-91",
                    DocumentStatus = "DRAFT",
                    OrderId = 88,
                    ItemId = 10,
                    Qty = 100,
                    IsEffective = true
                }]), RecoveryContext()),
            ("ledger lineage ambiguity", RecoveryFacts(movements:
                [Movement(1, 90, "INVENTORY_CORRECTION", 0)]), RecoveryContext())
        };

        foreach (var current in cases)
        {
            var decision = HuMutationEligibilityPolicy.Evaluate(current.Facts, current.Context);
            Assert.False(decision.Allowed, current.Name);
        }
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

    private static HuOperatorFacts RecoveryFacts(
        IReadOnlyList<HuOperatorStockFact>? stock = null,
        IReadOnlyList<HuOperatorProductionPalletFact>? pallets = null,
        IReadOnlyList<HuOperatorReservationFact>? reservations = null,
        IReadOnlyList<HuOperatorOutboundFact>? outbound = null,
        IReadOnlyList<HuOperatorLedgerMovementFact>? movements = null,
        long? componentOwnerOrderId = 77,
        string ownerOrderType = "CUSTOMER",
        double filledQty = 100) => new()
    {
        HuCode = "HU-RELEASE",
        Stock = stock ?? Array.Empty<HuOperatorStockFact>(),
        ProductionPallets = pallets ??
        [
            BuildRecoveryPallet(
                componentOwnerOrderId: componentOwnerOrderId,
                ownerOrderType: ownerOrderType,
                filledQty: filledQty)
        ],
        Reservations = reservations ?? Array.Empty<HuOperatorReservationFact>(),
        Outbound = outbound ?? Array.Empty<HuOperatorOutboundFact>(),
        LedgerMovements = movements ?? Array.Empty<HuOperatorLedgerMovementFact>()
    };

    private static HuOperatorProductionPalletFact BuildRecoveryPallet(
        long palletId = 10,
        long? componentOwnerOrderId = 77,
        string ownerOrderType = "CUSTOMER",
        double filledQty = 100) => new()
    {
        PalletId = palletId,
        Status = ProductionPalletStatus.Filled,
        OwnerOrderId = 77,
        OwnerOrderType = ownerOrderType,
        OwnerOrderStatus = "IN_PROGRESS",
        Components =
        [
            new HuOperatorComponentFact
            {
                OrderLineId = 701,
                OrderLineOrderId = componentOwnerOrderId,
                ItemId = 10,
                PlannedQty = 100,
                FilledQty = filledQty
            }
        ]
    };

    private static HuMutationEligibilityContext RecoveryContext(double requestedQty = 100) => new()
    {
        Operation = HuMutationOperation.ReleaseProducedStock,
        SourceOrderId = 77,
        RequestedComponents = [new HuMutationRequestedComponent(10, requestedQty)]
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
