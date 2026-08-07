using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.HuOperator;

public sealed class HuOperatorReadModelServiceTests
{
    [Fact]
    public void OrdinaryOrderProjection_HidesLabelNotPrintedAndShowsAwaitingFill()
    {
        var store = new FakeFactsStore
        {
            OrderFacts =
            [
                ProductionFacts("HU-PLANNED", ProductionPalletStatus.Planned, orderLineId: 701),
                ProductionFacts("HU-PRINTED", ProductionPalletStatus.Printed, orderLineId: 701)
            ]
        };
        var service = new HuOperatorReadModelService(store);

        var result = service.GetForOrder(orderId: 77);

        var line = Assert.Single(result);
        Assert.Equal(701, line.Key);
        var production = Assert.Single(line.Value.ProductionTasks);
        Assert.Equal("HU-PRINTED", production.HuCode);
        Assert.Equal(ProductionTaskSemanticCode.AwaitingFill, production.State.Code);
        Assert.Equal("Ожидает наполнения", production.State.Label);
        Assert.Empty(line.Value.OperationalHus);
        Assert.Equal(1, store.OrderCalls);
    }

    [Fact]
    public void OrderProjection_UsesCompactReservationLabelForCurrentOrder()
    {
        var store = new FakeFactsStore
        {
            OrderFacts =
            [
                new HuOperatorFacts
                {
                    HuCode = "HU-RESERVED",
                    Stock =
                    [
                        new HuOperatorStockFact
                        {
                            ItemId = 1,
                            ItemName = "Товар",
                            Uom = "шт",
                            LocationId = 5,
                            LocationCode = "MAIN",
                            LocationName = "Основной склад",
                            Qty = 100
                        }
                    ],
                    Reservations =
                    [
                        new HuOperatorReservationFact
                        {
                            OrderId = 77,
                            OrderRef = "ORD-77",
                            OrderType = "CUSTOMER",
                            OrderStatus = "ACCEPTED",
                            OrderLineId = 701,
                            ItemId = 1,
                            Qty = 100
                        }
                    ]
                }
            ]
        };

        var result = new HuOperatorReadModelService(store).GetForOrder(77);

        var row = Assert.Single(result[701].OperationalHus);
        Assert.Equal(OperationalHuSemanticCode.Reserved, row.State.Code);
        Assert.Equal("Зарезервирован", row.State.Label);
        Assert.Equal("MAIN", row.Location?.Code);
        Assert.Equal(77, row.ReservationTarget?.OrderId);
    }

    [Fact]
    public void SourceOrderProjection_KeepsHuReservedForAnotherOrderWithExplicitTargetLabel()
    {
        var production = ProductionFacts("HU-TRANSFERRED", ProductionPalletStatus.Filled, orderLineId: 701);
        var pallet = production.ProductionPallets.Single();
        var store = new FakeFactsStore
        {
            OrderFacts =
            [
                new HuOperatorFacts
                {
                    HuCode = production.HuCode,
                    Stock =
                    [
                        new HuOperatorStockFact
                        {
                            ItemId = 1,
                            ItemName = "Товар",
                            Uom = "шт",
                            LocationId = 5,
                            LocationCode = "MAIN",
                            Qty = 100
                        }
                    ],
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
                                    ItemName = "Товар",
                                    Uom = "шт",
                                    PlannedQty = 100,
                                    FilledQty = 100
                                }
                            ]
                        }
                    ],
                    Reservations =
                    [
                        new HuOperatorReservationFact
                        {
                            OrderId = 88,
                            OrderRef = "ORD-88",
                            OrderType = "CUSTOMER",
                            OrderStatus = "ACCEPTED",
                            OrderLineId = 801,
                            ItemId = 1,
                            Qty = 100
                        }
                    ]
                }
            ]
        };

        var result = new HuOperatorReadModelService(store).GetForOrder(77);

        var row = Assert.Single(result[701].OperationalHus);
        Assert.Equal(OperationalHuSemanticCode.Reserved, row.State.Code);
        Assert.Equal("Зарезервирован для заказа ORD-88", row.State.Label);
        Assert.Equal(100, row.Qty!.Value, 3);

        var global = HuOperatorReadModelService.ProjectGlobal(store.OrderFacts.Single());
        Assert.Equal(
            row.State.Code,
            Assert.IsType<OperationalHuPresentation>(global.OperatorPresentation.OperationalHu).State.Code);
    }

    [Fact]
    public void SourceOrderProjection_SeesWholeShipmentToAnotherOrderWithGlobalParity()
    {
        var source = ProductionFacts("HU-SHIPPED-OTHER", ProductionPalletStatus.Filled, orderLineId: 701);
        var sourcePallet = source.ProductionPallets.Single();
        source = new HuOperatorFacts
        {
            HuCode = source.HuCode,
            ProductionPallets =
            [
                new HuOperatorProductionPalletFact
                {
                    PalletId = sourcePallet.PalletId,
                    Status = sourcePallet.Status,
                    OwnerOrderId = 77,
                    OwnerOrderRef = "ORD-77",
                    OwnerOrderType = "CUSTOMER",
                    OwnerOrderStatus = "IN_PROGRESS",
                    Components = sourcePallet.Components.Select(component => new HuOperatorComponentFact
                    {
                        OrderLineId = component.OrderLineId,
                        OrderLineOrderId = component.OrderLineOrderId,
                        ItemId = component.ItemId,
                        ItemName = component.ItemName,
                        Uom = component.Uom,
                        PlannedQty = component.PlannedQty,
                        FilledQty = component.PlannedQty
                    }).ToArray()
                }
            ],
            Outbound =
            [
                new HuOperatorOutboundFact
                {
                    DocumentId = 91,
                    DocumentRef = "OUT-91",
                    DocumentStatus = "CLOSED",
                    OrderId = 88,
                    OrderRef = "ORD-88",
                    OrderType = "CUSTOMER",
                    OrderLineId = 801,
                    ItemId = 1,
                    ItemName = "Товар",
                    Uom = "шт",
                    Qty = 100
                }
            ],
            LedgerMovements =
            [
                Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100)
            ]
        };
        var store = new FakeFactsStore { OrderFacts = [source], HuFacts = source };
        var service = new HuOperatorReadModelService(store);

        var orderRow = Assert.Single(service.GetForOrder(77)[701].OperationalHus);
        var globalRow = Assert.IsType<OperationalHuPresentation>(
            service.GetForHu(source.HuCode).OperatorPresentation.OperationalHu);

        Assert.Equal(OperationalHuSemanticCode.Shipped, orderRow.State.Code);
        Assert.Equal(orderRow.State.Code, globalRow.State.Code);
        Assert.Equal(88, globalRow.ShipmentTarget?.OrderId);
    }

    [Fact]
    public void InconsistentPartialOutboundWarehouseHu_RemainsVisibleOnAffectedOrderLine()
    {
        var store = new FakeFactsStore
        {
            OrderFacts =
            [
                new HuOperatorFacts
                {
                    HuCode = "HU-PARTIAL-OUT",
                    Stock =
                    [
                        new HuOperatorStockFact
                        {
                            ItemId = 1,
                            ItemName = "Товар",
                            Uom = "шт",
                            LocationId = 5,
                            LocationCode = "MAIN",
                            Qty = 60
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
                            OrderRef = "ORD-77",
                            OrderLineId = 701,
                            ItemId = 1,
                            ItemName = "Товар",
                            Uom = "шт",
                            Qty = 40
                        }
                    ],
                    LedgerMovements =
                    [
                        Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                        Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -40)
                    ]
                }
            ]
        };

        var result = new HuOperatorReadModelService(store).GetForOrder(77);

        var row = Assert.Single(result[701].OperationalHus);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, row.State.Code);
        Assert.Equal("HU-PARTIAL-OUT", row.HuCode);
        Assert.Null(row.Qty);
    }

    [Fact]
    public void OperationalPresentation_UsesCurrentLedgerInsteadOfProductionPlanQuantity()
    {
        var store = new FakeFactsStore
        {
            HuFacts = new HuOperatorFacts
            {
                HuCode = "HU-CORRECTED-STOCK",
                Stock =
                [
                    new HuOperatorStockFact
                    {
                        ItemId = 1,
                        ItemName = "Товар",
                        Uom = "шт",
                        LocationId = 5,
                        LocationCode = "MAIN",
                        Qty = 80
                    }
                ],
                ProductionPallets =
                [
                    new HuOperatorProductionPalletFact
                    {
                        PalletId = 10,
                        Status = ProductionPalletStatus.Filled,
                        Components =
                        [
                            new HuOperatorComponentFact
                            {
                                ItemId = 1,
                                ItemName = "Товар",
                                Uom = "шт",
                                PlannedQty = 100,
                                FilledQty = 100
                            }
                        ]
                    }
                ]
            }
        };

        var presentation = Assert.IsType<OperationalHuPresentation>(
            new HuOperatorReadModelService(store)
                .GetForHu("HU-CORRECTED-STOCK")
                .OperatorPresentation.OperationalHu);

        Assert.Equal(80, presentation.Qty!.Value, 3);
        Assert.Equal(80, Assert.Single(presentation.Components).Qty, 3);
    }

    [Fact]
    public void ShippedWarehouseHuWithoutProduction_UsesEffectiveShipmentComposition()
    {
        var store = new FakeFactsStore
        {
            HuFacts = new HuOperatorFacts
            {
                HuCode = "HU-WAREHOUSE-SHIPPED",
                Outbound =
                [
                    new HuOperatorOutboundFact
                    {
                        DocumentId = 91,
                        DocumentRef = "OUT-91",
                        DocumentStatus = "CLOSED",
                        OrderId = 77,
                        OrderRef = "ORD-77",
                        OrderLineId = 701,
                        ItemId = 1,
                        ItemName = "Товар",
                        Uom = "шт",
                        Qty = 100
                    }
                ],
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                    Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100)
                ]
            }
        };

        var presentation = Assert.IsType<OperationalHuPresentation>(
            new HuOperatorReadModelService(store)
                .GetForHu("HU-WAREHOUSE-SHIPPED")
                .OperatorPresentation.OperationalHu);

        Assert.Equal(OperationalHuSemanticCode.Shipped, presentation.State.Code);
        Assert.Equal(100, presentation.Qty!.Value, 3);
        Assert.Equal(100, Assert.Single(presentation.Components).Qty, 3);
    }

    [Fact]
    public void ShippedOrderProjection_IgnoresDraftOutboundQuantity()
    {
        var facts = new HuOperatorFacts
        {
            HuCode = "HU-SHIPPED-WITH-DRAFT",
            Outbound =
            [
                Outbound(documentId: 91, status: "CLOSED", orderId: 77, orderLineId: 701, itemId: 1, qty: 100),
                Outbound(documentId: 92, status: "DRAFT", orderId: 77, orderLineId: 701, itemId: 1, qty: 40)
            ],
            LedgerMovements =
            [
                Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100)
            ]
        };
        var service = new HuOperatorReadModelService(new FakeFactsStore { OrderFacts = [facts] });

        var row = Assert.Single(service.GetForOrder(77)[701].OperationalHus);

        Assert.Equal(OperationalHuSemanticCode.Shipped, row.State.Code);
        Assert.Equal(100, row.Qty!.Value, 3);
    }

    [Fact]
    public void ShippedOrderProjection_UsesOnlyCurrentShipmentLifecycle()
    {
        var facts = new HuOperatorFacts
        {
            HuCode = "HU-RESHIPPED",
            Outbound =
            [
                Outbound(documentId: 91, status: "CLOSED", orderId: 77, orderLineId: 701, itemId: 1, qty: 100),
                Outbound(documentId: 93, status: "CLOSED", orderId: 88, orderLineId: 801, itemId: 1, qty: 100)
            ],
            LedgerMovements =
            [
                Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                Movement(2, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                Movement(3, 92, "INVENTORY_CORRECTION", itemId: 1, qtyDelta: 100),
                Movement(4, 93, "OUTBOUND", itemId: 1, qtyDelta: -100)
            ]
        };
        var service = new HuOperatorReadModelService(new FakeFactsStore { OrderFacts = [facts], HuFacts = facts });

        var sourceOrder = service.GetForOrder(77);
        var currentOrderRow = Assert.Single(service.GetForOrder(88)[801].OperationalHus);
        var global = Assert.IsType<OperationalHuPresentation>(
            service.GetForHu(facts.HuCode).OperatorPresentation.OperationalHu);

        Assert.Empty(sourceOrder);
        Assert.Equal(OperationalHuSemanticCode.Shipped, currentOrderRow.State.Code);
        Assert.Equal(100, currentOrderRow.Qty!.Value, 3);
        Assert.Equal(100, global.Qty!.Value, 3);
        Assert.Equal(88, global.ShipmentTarget?.OrderId);
    }

    [Fact]
    public void ShippedMixedHu_PreservesCurrentLineScopedQuantities()
    {
        var facts = new HuOperatorFacts
        {
            HuCode = "HU-MIXED-SHIPPED-LINES",
            Outbound =
            [
                Outbound(documentId: 91, status: "CLOSED", orderId: 77, orderLineId: 701, itemId: 1, qty: 100),
                Outbound(documentId: 91, status: "CLOSED", orderId: 77, orderLineId: 702, itemId: 2, qty: 50)
            ],
            LedgerMovements =
            [
                Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 100),
                Movement(2, 10, "INBOUND", itemId: 2, qtyDelta: 50),
                Movement(3, 91, "OUTBOUND", itemId: 1, qtyDelta: -100),
                Movement(4, 91, "OUTBOUND", itemId: 2, qtyDelta: -50)
            ]
        };

        var rows = new HuOperatorReadModelService(new FakeFactsStore { OrderFacts = [facts] }).GetForOrder(77);

        Assert.Equal(100, Assert.Single(rows[701].OperationalHus).Qty!.Value, 3);
        Assert.Equal(50, Assert.Single(rows[702].OperationalHus).Qty!.Value, 3);
    }

    [Fact]
    public void NegativeLedger_HasSameInconsistentStateInOrderAndGlobalProjections()
    {
        var facts = ProductionFacts("HU-NEGATIVE", ProductionPalletStatus.Filled, orderLineId: 701);
        facts = new HuOperatorFacts
        {
            HuCode = facts.HuCode,
            Stock =
            [
                new HuOperatorStockFact
                {
                    ItemId = 1,
                    ItemName = "Товар",
                    Uom = "шт",
                    LocationId = 5,
                    LocationCode = "MAIN",
                    Qty = -10
                }
            ],
            ProductionPallets = facts.ProductionPallets
        };
        var store = new FakeFactsStore { OrderFacts = [facts], HuFacts = facts };
        var service = new HuOperatorReadModelService(store);

        var orderRow = Assert.Single(service.GetForOrder(77)[701].OperationalHus);
        var globalRow = Assert.IsType<OperationalHuPresentation>(
            service.GetForHu("HU-NEGATIVE").OperatorPresentation.OperationalHu);

        Assert.Equal(OperationalHuSemanticCode.Inconsistent, orderRow.State.Code);
        Assert.Equal(orderRow.State.Code, globalRow.State.Code);
    }

    [Fact]
    public void MixedLedgerBackedHu_ExposesPerComponentQuantitiesWithoutScalarSum()
    {
        var store = new FakeFactsStore
        {
            HuFacts = new HuOperatorFacts
            {
                HuCode = "HU-MIXED-STOCK",
                Stock =
                [
                    new HuOperatorStockFact { ItemId = 1, ItemName = "Штуки", Uom = "шт", LocationId = 5, LocationCode = "MAIN", Qty = 10 },
                    new HuOperatorStockFact { ItemId = 2, ItemName = "Вес", Uom = "кг", LocationId = 5, LocationCode = "MAIN", Qty = 2.5 }
                ]
            }
        };

        var row = Assert.IsType<OperationalHuPresentation>(
            new HuOperatorReadModelService(store).GetForHu("HU-MIXED-STOCK").OperatorPresentation.OperationalHu);

        Assert.Null(row.Qty);
        Assert.Null(row.Uom);
        Assert.Collection(
            row.Components.OrderBy(component => component.ItemId),
            component => Assert.Equal(10, component.Qty, 3),
            component => Assert.Equal(2.5, component.Qty, 3));
    }

    [Fact]
    public void GlobalProjection_IncludesReservationTargetInLabel()
    {
        var store = new FakeFactsStore
        {
            HuFacts = new HuOperatorFacts
            {
                HuCode = "HU-RESERVED",
                RegistryKnown = true,
                Stock =
                [
                    new HuOperatorStockFact
                    {
                        ItemId = 1,
                        ItemName = "Товар",
                        Uom = "шт",
                        LocationId = 5,
                        LocationCode = "MAIN",
                        Qty = 100
                    }
                ],
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
            }
        };

        var result = new HuOperatorReadModelService(store).GetForHu("hu-reserved");

        Assert.True(result.Known);
        Assert.Null(result.OperatorPresentation.ProductionTask);
        var operational = Assert.IsType<OperationalHuPresentation>(result.OperatorPresentation.OperationalHu);
        Assert.Equal(OperationalHuSemanticCode.Reserved, operational.State.Code);
        Assert.Equal("Зарезервирован для заказа ORD-77", operational.State.Label);
        Assert.True(result.HistoryAvailable);
    }

    [Fact]
    public void ProductionProjection_ExposesLabelNotPrintedFromSameClassification()
    {
        var store = new FakeFactsStore
        {
            OrderFacts = [ProductionFacts("HU-PLANNED", ProductionPalletStatus.Planned, orderLineId: 701)]
        };
        var service = new HuOperatorReadModelService(store);

        var ordinary = service.GetForOrder(77);
        var production = service.GetProductionForOrder(77);

        Assert.Empty(ordinary);
        var task = Assert.Single(production);
        Assert.Equal(ProductionTaskSemanticCode.LabelNotPrinted, task.State.Code);
        Assert.Equal("Этикетка не напечатана", task.State.Label);
    }

    [Fact]
    public void LedgerHistoryWithoutCurrentBalance_IsKnownHistoryOnly()
    {
        var store = new FakeFactsStore
        {
            HuFacts = new HuOperatorFacts
            {
                HuCode = "HU-HISTORY",
                LedgerMovements =
                [
                    Movement(1, 10, "INBOUND", itemId: 1, qtyDelta: 10),
                    Movement(2, 11, "WRITE_OFF", itemId: 1, qtyDelta: -10)
                ]
            }
        };

        var result = new HuOperatorReadModelService(store).GetForHu("HU-HISTORY");

        Assert.True(result.Known);
        Assert.True(result.HistoryAvailable);
        Assert.Null(result.OperatorPresentation.ProductionTask);
        Assert.Null(result.OperatorPresentation.OperationalHu);
    }

    private static HuOperatorFacts ProductionFacts(string huCode, string status, long orderLineId) => new()
    {
        HuCode = huCode,
        ProductionPallets =
        [
            new HuOperatorProductionPalletFact
            {
                PalletId = orderLineId,
                Status = status,
                OwnerOrderId = 77,
                OwnerOrderRef = "ORD-77",
                OwnerOrderType = "CUSTOMER",
                OwnerOrderStatus = "IN_PROGRESS",
                Components =
                [
                    new HuOperatorComponentFact
                    {
                        OrderLineId = orderLineId,
                        OrderLineOrderId = 77,
                        ItemId = 1,
                        ItemName = "Товар",
                        Uom = "шт",
                        PlannedQty = 100
                    }
                ]
            }
        ]
    };

    private static HuOperatorLedgerMovementFact Movement(
        long ledgerId,
        long documentId,
        string documentType,
        long itemId,
        double qtyDelta) => new()
    {
        LedgerId = ledgerId,
        Timestamp = new DateTime(2026, 1, 1).AddMinutes(ledgerId),
        DocumentId = documentId,
        DocumentRef = $"DOC-{documentId}",
        DocumentType = documentType,
        DocumentStatus = "CLOSED",
        ItemId = itemId,
        ItemName = $"Товар {itemId}",
        Uom = "шт",
        LocationId = 5,
        LocationCode = "MAIN",
        QtyDelta = qtyDelta
    };

    private static HuOperatorOutboundFact Outbound(
        long documentId,
        string status,
        long orderId,
        long orderLineId,
        long itemId,
        double qty) => new()
    {
        DocumentId = documentId,
        DocumentRef = $"OUT-{documentId}",
        DocumentStatus = status,
        OrderId = orderId,
        OrderRef = $"ORD-{orderId}",
        OrderType = "CUSTOMER",
        OrderLineId = orderLineId,
        ItemId = itemId,
        ItemName = $"Товар {itemId}",
        Uom = "шт",
        Qty = qty
    };

    private sealed class FakeFactsStore : IHuOperatorFactsStore
    {
        public IReadOnlyList<HuOperatorFacts> OrderFacts { get; init; } = Array.Empty<HuOperatorFacts>();
        public HuOperatorFacts? HuFacts { get; init; }
        public int OrderCalls { get; private set; }

        public IReadOnlyList<HuOperatorFacts> GetForOrder(long orderId)
        {
            OrderCalls++;
            return OrderFacts;
        }

        public HuOperatorFacts? GetForHu(string huCode) =>
            HuFacts ?? OrderFacts.FirstOrDefault(row => string.Equals(row.HuCode, huCode, StringComparison.OrdinalIgnoreCase));
    }
}
