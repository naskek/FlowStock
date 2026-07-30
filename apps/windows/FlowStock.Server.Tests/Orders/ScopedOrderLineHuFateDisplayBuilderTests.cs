using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class ScopedOrderLineHuFateDisplayBuilderTests
{
    [Fact]
    public void Build_UsesExactRequestedKeysAndLatestShipmentWithoutGlobalReads()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var optimized = store.As<IOptimizedOrderLineHuFateStore>();
        optimized.Setup(data => data.GetScopedOrderLineHuFateCandidates(
                It.Is<IReadOnlyCollection<ScopedOrderLineHuFateKey>>(keys =>
                    keys.Count == 1
                    && keys.Single() == new ScopedOrderLineHuFateKey(5, "HU-1"))))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ReservationCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    TargetOrderId = 4,
                    TargetOrderLineId = 40,
                    TargetOrderRef = "004"
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ShipmentCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 8,
                    TargetOrderId = 4,
                    TargetOrderLineId = 40,
                    TargetOrderRef = "004",
                    DocId = 100,
                    DocRef = "OUT-OLD",
                    ClosedAt = new DateTime(2026, 6, 10, 10, 0, 0),
                    CreatedAt = new DateTime(2026, 6, 10, 9, 0, 0)
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ShipmentCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    TargetOrderId = 4,
                    TargetOrderLineId = 40,
                    TargetOrderRef = "004",
                    DocId = 101,
                    DocRef = "OUT-NEW",
                    ClosedAt = new DateTime(2026, 6, 11, 10, 0, 0),
                    CreatedAt = new DateTime(2026, 6, 11, 9, 0, 0)
                }
            ]);
        var timing = new OrderLineHuFateTiming();

        var rows = ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Internal },
            [new ScopedOrderLineHuFateSource { OrderLineId = 30, ItemId = 5, HuCode = " hu-1 ", Qty = 10 }],
            timing);

        var row = Assert.Single(rows[30]);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, row.FateCode);
        Assert.Equal("→ отгружено заказ 004", row.FateLabel);
        Assert.Equal("OUT-NEW", row.FateDocRef);
        Assert.Equal(10, row.FateQty);
        Assert.True(timing.Scoped);
        Assert.False(timing.Skipped);
        Assert.Equal(1, timing.ScopedKeysCount);
        Assert.Equal(0, timing.GetOrdersMs);
        Assert.Equal(0, timing.GetDocsMs);
        Assert.Equal(1, timing.SourcesCount);
        store.Verify(data => data.GetOrders(), Times.Never);
        store.Verify(data => data.GetDocs(), Times.Never);
        store.Verify(data => data.GetHuStockRows(), Times.Never);
    }

    [Fact]
    public void Build_UsesSameOrderShipmentLabel()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(It.IsAny<IReadOnlyCollection<ScopedOrderLineHuFateKey>>()))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ShipmentCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    TargetOrderId = 3,
                    TargetOrderLineId = 30,
                    TargetOrderRef = "003",
                    DocId = 101,
                    DocRef = "OUT-101",
                    ClosedAt = new DateTime(2026, 6, 11, 10, 0, 0)
                }
            ]);

        var row = Assert.Single(ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer },
            [new ScopedOrderLineHuFateSource { OrderLineId = 30, ItemId = 5, HuCode = "HU-1", Qty = 10 }])[30]);

        Assert.Equal("отгружено", row.FateLabel);
        Assert.Equal("003", row.FateOrderRef);
        Assert.Equal("OUT-101", row.FateDocRef);
    }

    [Fact]
    public void Build_UsesOtherOrderReservationWhenHuStillHasStock()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(It.IsAny<IReadOnlyCollection<ScopedOrderLineHuFateKey>>()))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ReservationCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    TargetOrderId = 4,
                    TargetOrderLineId = 40,
                    TargetOrderRef = "004"
                }
            ]);

        var row = Assert.Single(ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.InProgress },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = [new ScopedOrderLineHuFateKey(5, "HU-1")]
                }
            ])[30]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedFateCode, row.FateCode);
        Assert.Equal("→ резерв заказ 004", row.FateLabel);
        Assert.Equal("004", row.FateOrderRef);
        Assert.Equal(10, row.FateQty);
    }

    [Fact]
    public void Build_CustomerCompleteProductionPalletWithStock_UsesAwaitingShipment()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(It.IsAny<IReadOnlyCollection<ScopedOrderLineHuFateKey>>()))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10
                }
            ]);

        var row = Assert.Single(ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.InProgress },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = [new ScopedOrderLineHuFateKey(5, "HU-1")]
                }
            ])[30]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, row.FateCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel, row.Label);
        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel, row.FateLabel);
        Assert.Equal(10, row.FateQty);
    }

    [Fact]
    public void Build_CustomerProductionPalletWithoutNormalizedOwnership_RemainsOnStock()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(It.IsAny<IReadOnlyCollection<ScopedOrderLineHuFateKey>>()))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10
                }
            ]);

        var row = Assert.Single(ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.InProgress },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-1",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = false,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = [new ScopedOrderLineHuFateKey(5, "HU-1")]
                }
            ])[30]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, row.FateCode);
        Assert.NotEqual(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, row.FateCode);
    }

    [Fact]
    public void Build_CustomerMixedProductionPallet_MissingComponentStockSuppressesAwaiting()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(
                It.Is<IReadOnlyCollection<ScopedOrderLineHuFateKey>>(keys => keys.Count == 2)))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10
                }
            ]);
        var componentKeys = new[]
        {
            new ScopedOrderLineHuFateKey(5, "HU-MIXED"),
            new ScopedOrderLineHuFateKey(6, "HU-MIXED")
        };

        var rows = ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.Accepted },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                },
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 31,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                }
            ]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, Assert.Single(rows[30]).FateCode);
        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void Build_CustomerMixedProductionPallet_ReservationOnOneComponentSuppressesAwaitingForWholePallet()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(
                It.Is<IReadOnlyCollection<ScopedOrderLineHuFateKey>>(keys => keys.Count == 2)))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ReservationCandidateKind,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5,
                    TargetOrderId = 4,
                    TargetOrderLineId = 40,
                    TargetOrderRef = "004"
                }
            ]);
        var componentKeys = new[]
        {
            new ScopedOrderLineHuFateKey(5, "HU-MIXED"),
            new ScopedOrderLineHuFateKey(6, "HU-MIXED")
        };

        var rows = ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.InProgress },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                },
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 31,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                }
            ]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, Assert.Single(rows[30]).FateCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedFateCode, Assert.Single(rows[31]).FateCode);
        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void Build_CustomerMixedProductionPallet_ShipmentOnOneComponentSuppressesAwaitingForWholePallet()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(
                It.Is<IReadOnlyCollection<ScopedOrderLineHuFateKey>>(keys => keys.Count == 2)))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.ShipmentCandidateKind,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 2,
                    TargetOrderId = 4,
                    TargetOrderLineId = 40,
                    TargetOrderRef = "004",
                    DocId = 200,
                    DocRef = "OUT-200",
                    ClosedAt = new DateTime(2026, 6, 11, 10, 0, 0)
                }
            ]);
        var componentKeys = new[]
        {
            new ScopedOrderLineHuFateKey(5, "HU-MIXED"),
            new ScopedOrderLineHuFateKey(6, "HU-MIXED")
        };

        var rows = ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.InProgress },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                },
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 31,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                }
            ]);

        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, Assert.Single(rows[30]).FateCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, Assert.Single(rows[31]).FateCode);
        Assert.DoesNotContain(
            rows.Values.SelectMany(entries => entries),
            entry => entry.FateCode == OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode);
    }

    [Fact]
    public void Build_CustomerMixedProductionPallet_WithoutBlockersAwaitsForEveryComponent()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.As<IOptimizedOrderLineHuFateStore>()
            .Setup(data => data.GetScopedOrderLineHuFateCandidates(
                It.Is<IReadOnlyCollection<ScopedOrderLineHuFateKey>>(keys => keys.Count == 2)))
            .Returns(
            [
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10
                },
                new ScopedOrderLineHuFateCandidate
                {
                    Kind = ScopedOrderLineHuFateDisplayBuilder.StockCandidateKind,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5
                }
            ]);
        var componentKeys = new[]
        {
            new ScopedOrderLineHuFateKey(5, "HU-MIXED"),
            new ScopedOrderLineHuFateKey(6, "HU-MIXED")
        };

        var rows = ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Customer, Status = OrderStatus.Accepted },
            [
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 30,
                    ItemId = 5,
                    HuCode = "HU-MIXED",
                    Qty = 10,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                },
                new ScopedOrderLineHuFateSource
                {
                    OrderLineId = 31,
                    ItemId = 6,
                    HuCode = "HU-MIXED",
                    Qty = 5,
                    ProductionPalletId = 100,
                    IsOwnedBySourceOrder = true,
                    IsProductionPalletComplete = true,
                    ProductionPalletComponentKeys = componentKeys
                }
            ]);

        Assert.Equal(
            OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode,
            Assert.Single(rows[30]).FateCode);
        Assert.Equal(
            OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode,
            Assert.Single(rows[31]).FateCode);
        Assert.Equal(10, Assert.Single(rows[30]).FateQty);
        Assert.Equal(5, Assert.Single(rows[31]).FateQty);
    }

    [Fact]
    public void Build_WithoutOptimizedStoreLeavesFateEmptyWithoutGlobalFallback()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var timing = new OrderLineHuFateTiming();

        var rows = ScopedOrderLineHuFateDisplayBuilder.Build(
            store.Object,
            new Order { Id = 3, OrderRef = "003", Type = OrderType.Internal },
            [new ScopedOrderLineHuFateSource { OrderLineId = 30, ItemId = 5, HuCode = "HU-1", Qty = 10 }],
            timing);

        Assert.Empty(rows);
        Assert.True(timing.Scoped);
        Assert.True(timing.Skipped);
        Assert.Equal(0, timing.TotalMs);
        store.VerifyNoOtherCalls();
    }
}
