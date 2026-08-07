using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.HuOperator;

public sealed class HuOperatorClassifierTests
{
    [Fact]
    public void PlannedProductionHuWithoutLedger_IsLabelNotPrintedProductionTask()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-PLAN-1",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Planned,
                    Components =
                    [
                        new HuOperatorComponentFact
                        {
                            ItemId = 1,
                            ItemName = "Товар",
                            Uom = "шт",
                            PlannedQty = 100
                        }
                    ]
                }
            ]
        });

        var production = Assert.IsType<HuOperatorProductionClassification>(result);
        Assert.Equal(ProductionTaskSemanticCode.LabelNotPrinted, production.StateCode);
    }

    [Fact]
    public void PrintedProductionHuWithoutLedger_IsAwaitingFillProductionTask()
    {
        var result = HuOperatorClassifier.Classify(SinglePalletFacts(ProductionPalletStatus.Printed));

        var production = Assert.IsType<HuOperatorProductionClassification>(result);
        Assert.Equal(ProductionTaskSemanticCode.AwaitingFill, production.StateCode);
    }

    [Fact]
    public void MixedProductionHuWithSomeCompletedComponents_IsFillingProductionTask()
    {
        var facts = SinglePalletFacts(ProductionPalletStatus.Printed);
        facts = new HuOperatorFacts
        {
            HuCode = facts.HuCode,
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Printed,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 100 },
                        new HuOperatorComponentFact { ItemId = 2, PlannedQty = 50, FilledQty = 0 }
                    ]
                }
            ]
        };

        var production = Assert.IsType<HuOperatorProductionClassification>(HuOperatorClassifier.Classify(facts));

        Assert.Equal(ProductionTaskSemanticCode.Filling, production.StateCode);
        Assert.Equal(1, production.CompletedComponents);
        Assert.Equal(2, production.TotalComponents);
    }

    [Fact]
    public void MixedProductionHuWithPartialComponentQuantity_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-MIXED-CORRUPT",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Printed,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 50 },
                        new HuOperatorComponentFact { ItemId = 2, PlannedQty = 100, FilledQty = 0 }
                    ]
                }
            ]
        });

        Assert.Equal(
            OperationalHuSemanticCode.Inconsistent,
            Assert.IsType<HuOperatorOperationalClassification>(result).StateCode);
    }

    [Fact]
    public void MixedProductionHuWithCompletedAndPartialComponents_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-MIXED-CORRUPT-2",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Printed,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 100 },
                        new HuOperatorComponentFact { ItemId = 2, PlannedQty = 100, FilledQty = 50 }
                    ]
                }
            ]
        });

        Assert.Equal(
            OperationalHuSemanticCode.Inconsistent,
            Assert.IsType<HuOperatorOperationalClassification>(result).StateCode);
    }

    [Fact]
    public void PersistedFilledPalletWithIncompleteComponent_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-FILLED-CORRUPT",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Filled,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 100 },
                        new HuOperatorComponentFact { ItemId = 2, PlannedQty = 100, FilledQty = 0 }
                    ]
                }
            ]
        });

        Assert.Equal(
            OperationalHuSemanticCode.Inconsistent,
            Assert.IsType<HuOperatorOperationalClassification>(result).StateCode);
    }

    [Fact]
    public void FilledProductionHuWithoutLedger_IsReleaseNotPostedProductionTask()
    {
        var production = Assert.IsType<HuOperatorProductionClassification>(
            HuOperatorClassifier.Classify(SinglePalletFacts(ProductionPalletStatus.Filled)));

        Assert.Equal(ProductionTaskSemanticCode.ReleaseNotPosted, production.StateCode);
    }

    [Fact]
    public void PositiveLedgerWithoutOperationalOwner_IsOnStock()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-STOCK",
            Stock =
            [
                new HuOperatorStockFact
                {
                    ItemId = 1,
                    ItemName = "Товар",
                    LocationId = 5,
                    LocationCode = "MAIN",
                    Qty = 100
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.OnStock, operational.StateCode);
    }

    [Fact]
    public void PositiveLedgerWithOneActiveCustomerReservation_IsReserved()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-RESERVED",
            Stock = [Stock(itemId: 1, qty: 100)],
            Reservations =
            [
                new HuOperatorReservationFact
                {
                    OrderId = 77,
                    OrderRef = "ORD-77",
                    OrderType = "CUSTOMER",
                    OrderStatus = "ACCEPTED",
                    ItemId = 1,
                    Qty = 100
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Reserved, operational.StateCode);
        Assert.Equal(77, operational.ReservationTarget?.OrderId);
    }

    [Fact]
    public void PartialReservationOfSingleHu_IsInconsistent()
    {
        var reservation = Reservation(orderId: 77, itemId: 1);
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-PARTIAL-RESERVATION",
            Stock = [Stock(itemId: 1, qty: 100)],
            Reservations =
            [
                new HuOperatorReservationFact
                {
                    OrderId = reservation.OrderId,
                    OrderRef = reservation.OrderRef,
                    OrderType = reservation.OrderType,
                    OrderStatus = reservation.OrderStatus,
                    ItemId = reservation.ItemId,
                    Qty = 40
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.MixedOperationalTargetConflict);
    }

    [Fact]
    public void CustomerOwnedFilledHuWithPositiveLedger_IsAwaitingShipment()
    {
        var palletFacts = SinglePalletFacts(ProductionPalletStatus.Filled);
        var pallet = palletFacts.ProductionPallets.Single();
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-CUSTOMER",
            Stock = [Stock(itemId: 1, qty: 100)],
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = pallet.PalletId,
                    Status = pallet.Status,
                    OwnerOrderId = 77,
                    OwnerOrderRef = "ORD-77",
                    OwnerOrderType = "CUSTOMER",
                    OwnerOrderStatus = "IN_PROGRESS",
                    Components =
                    [
                        new HuOperatorComponentFact
                        {
                            OrderLineId = 701,
                            OrderLineOrderId = 77,
                            ItemId = 1,
                            PlannedQty = 100,
                            FilledQty = 100
                        }
                    ]
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.AwaitingShipment, operational.StateCode);
    }

    [Fact]
    public void CustomerOwnedFilledHuWithIncompleteLedgerComposition_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-CUSTOMER-MISMATCH",
            Stock = [Stock(itemId: 1, qty: 80)],
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Filled,
                    OwnerOrderId = 77,
                    OwnerOrderRef = "ORD-77",
                    OwnerOrderType = "CUSTOMER",
                    OwnerOrderStatus = "IN_PROGRESS",
                    Components =
                    [
                        new HuOperatorComponentFact
                        {
                            OrderLineId = 701,
                            OrderLineOrderId = 77,
                            ItemId = 1,
                            PlannedQty = 100,
                            FilledQty = 100
                        }
                    ]
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.ProductionLedgerContradiction);
    }

    [Fact]
    public void ClosedOutboundWithPositiveLedgerRemainder_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-PARTIAL",
            Stock = [Stock(itemId: 1, qty: 60)],
            Outbound =
            [
                new HuOperatorOutboundFact
                {
                    DocumentId = 91,
                    DocumentRef = "OUT-17",
                    DocumentStatus = "CLOSED",
                    OrderId = 77,
                    OrderRef = "ORD-77",
                    OrderType = "CUSTOMER",
                    ItemId = 1,
                    Qty = 40
                }
            ],
            LedgerMovements =
            [
                Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -40)
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.PartialClosedOutboundWithRemainder);
    }

    [Fact]
    public void WholeClosedOutboundWithZeroLedger_IsShipped()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-SHIPPED",
            Outbound =
            [
                new HuOperatorOutboundFact
                {
                    DocumentId = 91,
                    DocumentRef = "OUT-17",
                    DocumentStatus = "CLOSED",
                    OrderId = 77,
                    OrderRef = "ORD-77",
                    OrderType = "CUSTOMER",
                    ItemId = 1,
                    Qty = 100
                }
            ],
            LedgerMovements =
            [
                Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100)
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Shipped, operational.StateCode);
        Assert.Equal(77, operational.ShipmentTarget?.OrderId);
    }

    [Fact]
    public void WholeShipmentThenClosedInventoryCorrection_UsesRestoredCurrentStock()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-RESTORED",
                Stock = [Stock(itemId: 1, qty: 100)],
                Outbound = [ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91)],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(3, 92, "INVENTORY_CORRECTION", itemId: 1, qtyDelta: 100)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.OnStock, operational.StateCode);
    }

    [Fact]
    public void WholeShipmentThenClosedInventoryCorrectionAndReservation_IsReserved()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-RESTORED-RESERVED",
                Stock = [Stock(itemId: 1, qty: 100)],
                Reservations = [Reservation(orderId: 88, itemId: 1)],
                Outbound = [ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91)],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(3, 92, "INVENTORY_CORRECTION", itemId: 1, qtyDelta: 100)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.Reserved, operational.StateCode);
        Assert.Equal(88, operational.ReservationTarget?.OrderId);
    }

    [Fact]
    public void WholeShipmentRestorationAndNormalMove_UsesCurrentStockAtNewLocation()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-RESTORED-MOVED",
                Stock =
                [
                    new HuOperatorStockFact
                    {
                        ItemId = 1,
                        ItemName = "Товар 1",
                        LocationId = 6,
                        LocationCode = "SECOND",
                        Qty = 100
                    }
                ],
                Outbound = [ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91)],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(3, 92, "INVENTORY_CORRECTION", itemId: 1, qtyDelta: 100),
                    Movement(4, 94, "MOVE", itemId: 1, qtyDelta: -100),
                    Movement(5, 94, "MOVE", itemId: 1, qtyDelta: 100, locationId: 6)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.OnStock, operational.StateCode);
    }

    [Fact]
    public void WholeShipmentRestorationAndSecondWholeShipment_UsesSecondShipmentLifecycle()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-RESHIPPED",
                Outbound =
                [
                    ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91),
                    ClosedOutbound(orderId: 88, itemId: 1, qty: 100, documentId: 93)
                ],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(3, 92, "INVENTORY_CORRECTION", itemId: 1, qtyDelta: 100),
                    Movement(4, 93, "OUTBOUND", itemId: 1, qtyDelta: -100)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.Shipped, operational.StateCode);
        Assert.Equal(88, operational.ShipmentTarget?.OrderId);
        Assert.Equal(93, operational.CurrentShipmentDocumentId);
    }

    [Fact]
    public void AmbiguousRepeatedShipmentHistory_IsInconsistent()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-RESHIPPED-AMBIGUOUS",
                Outbound =
                [
                    ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91),
                    ClosedOutbound(orderId: 88, itemId: 1, qty: 100, documentId: 93)
                ],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(3, 92, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(4, 93, "OUTBOUND", itemId: 1, qtyDelta: -100)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void PositiveStockAfterOutboundWithoutProvenCorrection_IsInconsistent()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-UNCERTAIN-RESTORE",
                Stock = [Stock(itemId: 1, qty: 100)],
                Outbound = [ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91)],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(3, 93, "INBOUND", itemId: 1, qtyDelta: 100)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void ClosedOutboundWithoutLedgerHistory_IsInconsistent()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-UNCERTAIN",
                Outbound = [ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91)]
            }));

        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void SupersededClosedOutboundLine_DoesNotAffectCurrentStockState()
    {
        var oldLine = ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91);
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-SUPERSEDED",
                Stock = [Stock(itemId: 1, qty: 100)],
                Outbound =
                [
                    new HuOperatorOutboundFact
                    {
                        DocumentId = oldLine.DocumentId,
                        DocumentRef = oldLine.DocumentRef,
                        DocumentStatus = oldLine.DocumentStatus,
                        OrderId = oldLine.OrderId,
                        OrderRef = oldLine.OrderRef,
                        OrderType = oldLine.OrderType,
                        ItemId = oldLine.ItemId,
                        Qty = oldLine.Qty,
                        IsEffective = false
                    }
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.OnStock, operational.StateCode);
    }

    [Fact]
    public void ClosedOutboundWithSeveralTargets_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-SHIP-CONFLICT",
            Outbound =
            [
                ClosedOutbound(orderId: 77, itemId: 1, qty: 50, documentId: 91),
                ClosedOutbound(orderId: 78, itemId: 1, qty: 50, documentId: 92)
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void ActiveReservationWithoutLedger_IsInconsistent()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-RESERVATION-NO-STOCK",
                Reservations = [Reservation(orderId: 77, itemId: 1)]
            }));

        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.ProductionLedgerContradiction);
    }

    [Fact]
    public void DraftOutboundDoesNotChangeDominantStockState()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-DRAFT",
                Stock = [Stock(itemId: 1, qty: 100)],
                Outbound =
                [
                    new HuOperatorOutboundFact
                    {
                        DocumentId = 91,
                        DocumentRef = "OUT-17",
                        DocumentStatus = "DRAFT",
                        OrderId = 77,
                        ItemId = 1,
                        Qty = 100
                    }
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.OnStock, operational.StateCode);
    }

    [Fact]
    public void WholeMixedHuClosedOutboundWithZeroLedger_IsShipped()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-MIXED-SHIPPED",
                ProductionPallets =
                [
                    new HuOperatorProductionPalletFact
                    {
                        PalletId = 10,
                        Status = ProductionPalletStatus.Filled,
                        Components =
                        [
                            new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 100 },
                            new HuOperatorComponentFact { ItemId = 2, PlannedQty = 50, FilledQty = 50 }
                        ]
                    }
                ],
                Outbound =
                [
                    ClosedOutbound(orderId: 77, itemId: 1, qty: 100, documentId: 91),
                    ClosedOutbound(orderId: 77, itemId: 2, qty: 50, documentId: 91)
                ],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 10, "INBOUND", itemId: 2, qtyDelta: 50),
                    Movement(3, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                    Movement(4, 91, "OUTBOUND", itemId: 2, qtyDelta: -50)
                ]
            }));

        Assert.Equal(OperationalHuSemanticCode.Shipped, operational.StateCode);
    }

    [Fact]
    public void CancelledProductionHistoryHasNoCurrentPresentation()
    {
        var result = HuOperatorClassifier.Classify(SinglePalletFacts(ProductionPalletStatus.Cancelled));

        Assert.IsType<HuOperatorNoCurrentClassification>(result);
    }

    [Fact]
    public void PositiveLedgerWithConflictingReservations_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-CONFLICT",
            Stock = [Stock(itemId: 1, qty: 100)],
            Reservations =
            [
                Reservation(orderId: 77, itemId: 1),
                Reservation(orderId: 78, itemId: 1)
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.ConflictingActiveReservations);
    }

    [Fact]
    public void MixedHuReservedForOnlyOneComponent_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-MIXED-RESERVATION",
            Stock = [Stock(itemId: 1, qty: 100), Stock(itemId: 2, qty: 50)],
            Reservations = [Reservation(orderId: 77, itemId: 1)]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.MixedOperationalTargetConflict);
    }

    [Fact]
    public void PositiveLedgerInSeveralLocations_IsInconsistent()
    {
        var first = Stock(itemId: 1, qty: 50);
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-LOCATIONS",
            Stock =
            [
                first,
                new HuOperatorStockFact
                {
                    ItemId = 1,
                    ItemName = first.ItemName,
                    LocationId = 6,
                    LocationCode = "SECOND",
                    Qty = 50
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
    }

    [Fact]
    public void NegativeLedgerBalance_IsInconsistent()
    {
        var operational = Assert.IsType<HuOperatorOperationalClassification>(
            HuOperatorClassifier.Classify(new HuOperatorFacts
            {
                HuCode = "HU-NEGATIVE",
                Stock = [Stock(itemId: 1, qty: -10)]
            }));

        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.ProductionLedgerContradiction);
    }

    [Fact]
    public void UnfilledProductionHuWithLedger_IsInconsistent()
    {
        var facts = SinglePalletFacts(ProductionPalletStatus.Printed);
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = facts.HuCode,
            Stock = [Stock(itemId: 1, qty: 100)],
            ProductionPallets = facts.ProductionPallets
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.ProductionLedgerContradiction);
    }

    [Fact]
    public void SeveralActiveProductionPalletsForSameHu_AreInconsistent()
    {
        var first = SinglePalletFacts(ProductionPalletStatus.Printed).ProductionPallets.Single();
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-DUPLICATE",
            ProductionPallets =
            [
                first,
                new HuOperatorProductionPalletFact
                {
                    PalletId = 11,
                    Status = ProductionPalletStatus.Printed,
                    Components = first.Components
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void MixedHuWithOnlyOneComponentShipped_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-MIXED-SHIPMENT",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Filled,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 100 },
                        new HuOperatorComponentFact { ItemId = 2, PlannedQty = 50, FilledQty = 50 }
                    ]
                }
            ],
            Outbound =
            [
                new HuOperatorOutboundFact
                {
                    DocumentId = 91,
                    DocumentRef = "OUT-17",
                    DocumentStatus = "CLOSED",
                    OrderId = 77,
                    OrderRef = "ORD-77",
                    ItemId = 1,
                    Qty = 100
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
        Assert.Contains(
            operational.DiagnosticReasons ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void SingleItemProductionWithPartialProgress_IsInconsistent()
    {
        var result = HuOperatorClassifier.Classify(new HuOperatorFacts
        {
            HuCode = "HU-SINGLE-PARTIAL",
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = 10,
                    Status = ProductionPalletStatus.Printed,
                    Components =
                    [
                        new HuOperatorComponentFact { ItemId = 1, PlannedQty = 100, FilledQty = 40 }
                    ]
                }
            ]
        });

        var operational = Assert.IsType<HuOperatorOperationalClassification>(result);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.StateCode);
    }

    private static HuOperatorFacts SinglePalletFacts(string status) => new()
    {
        HuCode = "HU-1",
        ProductionPallets =
        [
            new HuOperatorProductionPalletFact
            {
                PalletId = 10,
                Status = status,
                Components =
                [
                    new HuOperatorComponentFact
                    {
                        ItemId = 1,
                        ItemName = "Товар",
                        Uom = "шт",
                        PlannedQty = 100,
                        FilledQty = string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                            ? 100
                            : 0
                    }
                ]
            }
        ]
    };

    private static HuOperatorStockFact Stock(long itemId, double qty) => new()
    {
        ItemId = itemId,
        ItemName = $"Товар {itemId}",
        LocationId = 5,
        LocationCode = "MAIN",
        Qty = qty
    };

    private static HuOperatorReservationFact Reservation(long orderId, long itemId) => new()
    {
        OrderId = orderId,
        OrderRef = $"ORD-{orderId}",
        OrderType = "CUSTOMER",
        OrderStatus = "ACCEPTED",
        ItemId = itemId,
        Qty = 100
    };

    private static HuOperatorOutboundFact ClosedOutbound(
        long orderId,
        long itemId,
        double qty,
        long documentId) => new()
    {
        DocumentId = documentId,
        DocumentRef = $"OUT-{documentId}",
        DocumentStatus = "CLOSED",
        OrderId = orderId,
        OrderRef = $"ORD-{orderId}",
        OrderType = "CUSTOMER",
        ItemId = itemId,
        Qty = qty
    };

    private static HuOperatorLedgerMovementFact Movement(
        long ledgerId,
        long documentId,
        string documentType,
        long itemId,
        double qtyDelta,
        long locationId = 5) => new()
    {
        LedgerId = ledgerId,
        Timestamp = new DateTime(2026, 1, 1).AddMinutes(ledgerId),
        DocumentId = documentId,
        DocumentRef = $"DOC-{documentId}",
        DocumentType = documentType,
        DocumentStatus = "CLOSED",
        ItemId = itemId,
        ItemName = $"Товар {itemId}",
        LocationId = locationId,
        LocationCode = locationId == 5 ? "MAIN" : $"LOC-{locationId}",
        QtyDelta = qtyDelta
    };
}
