using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;

namespace FlowStock.Server.Tests.Tsd;

public sealed class HuResolverPostgresRegressionTests
{
    [Fact]
    public async Task SupersededShipmentAwaitsActiveReplacementBlocksAndZeroStockShips()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, store =>
        {
            var fixture = SeedFilledProductionHu(store);
            Assert.Equal(TsdHuState.AwaitingShipment, Resolve(store, fixture.HuCode).State);

            var outboundRef = $"OUT-HU-{Suffix()}";
            var outboundId = store.AddDoc(new Doc
            {
                DocRef = outboundRef,
                Type = DocType.Outbound,
                Status = DocStatus.Closed,
                CreatedAt = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                ClosedAt = new DateTime(2026, 7, 30, 10, 5, 0, DateTimeKind.Utc),
                OrderId = fixture.OrderId,
                OrderRef = fixture.OrderRef
            });
            var predecessorId = store.AddDocLine(new DocLine
            {
                DocId = outboundId,
                OrderLineId = fixture.OrderLineIds[0],
                ItemId = fixture.ItemIds[0],
                Qty = 17,
                FromLocationId = fixture.LocationId,
                FromHu = fixture.HuCode
            });
            var tombstoneId = store.AddDocLine(new DocLine
            {
                DocId = outboundId,
                ReplacesLineId = predecessorId,
                OrderLineId = fixture.OrderLineIds[0],
                ItemId = fixture.ItemIds[0],
                Qty = 0,
                FromLocationId = fixture.LocationId,
                FromHu = fixture.HuCode
            });

            var superseded = Resolve(store, fixture.HuCode);
            Assert.Equal(TsdHuState.AwaitingShipment, superseded.State);
            Assert.DoesNotContain(superseded.Documents, document => document.DocId == outboundId);

            const double replacementQty = 19;
            store.AddDocLine(new DocLine
            {
                DocId = outboundId,
                ReplacesLineId = tombstoneId,
                OrderLineId = fixture.OrderLineIds[0],
                ItemId = fixture.ItemIds[0],
                Qty = replacementQty,
                FromLocationId = fixture.LocationId,
                FromHu = fixture.HuCode
            });

            var activeReplacement = Resolve(store, fixture.HuCode);
            Assert.Equal(TsdHuState.FilledProductionPallet, activeReplacement.State);
            var activeDocument = Assert.Single(
                activeReplacement.Documents,
                document => document.DocId == outboundId);
            Assert.Equal(replacementQty, activeDocument.Qty);
            Assert.Equal(outboundRef, activeDocument.DocRef);

            store.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = new DateTime(2026, 7, 30, 10, 6, 0, DateTimeKind.Utc),
                DocId = outboundId,
                ItemId = fixture.ItemIds[0],
                LocationId = fixture.LocationId,
                QtyDelta = -fixture.Quantities[0],
                HuCode = fixture.HuCode
            });

            Assert.Equal(TsdHuState.Shipped, Resolve(store, fixture.HuCode).State);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MixedHuPreservesOperationalPriorityAndPartialShipmentUsesFilledFallback()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, store =>
        {
            var fixture = SeedFilledProductionHu(store, mixed: true);
            Assert.Equal(TsdHuState.AwaitingShipment, Resolve(store, fixture.HuCode).State);

            var closedOutboundId = AddOutbound(
                store,
                fixture,
                DocStatus.Closed,
                fixture.ItemIds[0],
                fixture.OrderLineIds[0]);
            Assert.Equal(
                TsdHuState.FilledProductionPallet,
                Resolve(store, fixture.HuCode).State);

            store.ReplaceOrderReceiptPlanLines(
                fixture.OrderId,
                [
                    new OrderReceiptPlanLine
                    {
                        OrderId = fixture.OrderId,
                        OrderLineId = fixture.OrderLineIds[1],
                        ItemId = fixture.ItemIds[1],
                        QtyPlanned = fixture.Quantities[1],
                        ToLocationId = fixture.LocationId,
                        ToHu = fixture.HuCode
                    }
                ]);
            Assert.Equal(TsdHuState.OutboundExpected, Resolve(store, fixture.HuCode).State);

            store.ReplaceOrderReceiptPlanLines(fixture.OrderId, Array.Empty<OrderReceiptPlanLine>());
            AddOutbound(
                store,
                fixture,
                DocStatus.Draft,
                fixture.ItemIds[1],
                fixture.OrderLineIds[1]);
            Assert.Equal(TsdHuState.OutboundPicked, Resolve(store, fixture.HuCode).State);
            Assert.True(closedOutboundId > 0);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DuplicateMixedComponentKeyUsesOneLedgerBalance()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, store =>
        {
            var fixture = SeedFilledProductionHu(store, mixed: true, duplicateItemKey: true);

            var result = Resolve(store, fixture.HuCode);

            Assert.Equal(TsdHuState.AwaitingShipment, result.State);
            var storeResult = ((ITsdHuResolverStore)store).GetTsdHuFacts(fixture.HuCode);
            var candidate = Assert.Single(storeResult.AwaitingShipmentCandidates);
            Assert.Equal(2, candidate.Components.Count);
            Assert.Single(candidate.ComponentKeys);
            Assert.Equal(fixture.Quantities.Sum(), candidate.ComponentKeys[0].LedgerBalance);
            return Task.CompletedTask;
        });
    }

    private static FilledHuFixture SeedFilledProductionHu(
        IDataStore store,
        bool mixed = false,
        bool duplicateItemKey = false)
    {
        EnsureAtLeastOneLocation(store);
        var locationId = store.GetLocations().First().Id;
        var suffix = Suffix();
        var partnerId = store.AddPartner(new Partner
        {
            Name = $"TSD resolver клиент {suffix}",
            Code = $"TSD-R-{suffix}"
        });
        var firstItemId = store.AddItem(new Item
        {
            Name = $"TSD resolver товар A {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        var secondItemId = duplicateItemKey
            ? firstItemId
            : store.AddItem(new Item
            {
                Name = $"TSD resolver товар B {suffix}",
                BaseUom = "шт",
                MaxQtyPerHu = 600
            });
        var orderRef = $"TSD-{suffix}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var group = mixed ? $"MIX-{suffix}" : null;
        var quantities = mixed ? new[] { 300d, 300d } : new[] { 600d };
        var itemIds = mixed ? new[] { firstItemId, secondItemId } : new[] { firstItemId };
        var orderLineIds = itemIds
            .Select((itemId, index) => store.AddOrderLine(new OrderLine
            {
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = quantities[index],
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ProductionPalletGroup = group
            }))
            .ToArray();

        var plan = new ProductionPalletService(store).PlanOrder(orderId);
        var pallet = Assert.Single(store.GetProductionPalletsByDoc(plan.PrdDocId));
        if (mixed)
        {
            Assert.Equal(2, pallet.Lines.Count);
        }
        store.MarkProductionPalletFilled(
            pallet.Id,
            new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc),
            "TSD-RESOLVER-TEST");

        foreach (var itemGroup in itemIds
                     .Select((itemId, index) => new { ItemId = itemId, Qty = quantities[index] })
                     .GroupBy(row => row.ItemId))
        {
            store.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = new DateTime(2026, 7, 30, 9, 1, 0, DateTimeKind.Utc),
                DocId = pallet.PrdDocId,
                ItemId = itemGroup.Key,
                LocationId = locationId,
                QtyDelta = itemGroup.Sum(row => row.Qty),
                HuCode = pallet.HuCode
            });
        }

        return new FilledHuFixture(
            orderId,
            orderRef,
            orderLineIds,
            itemIds,
            quantities,
            locationId,
            pallet.HuCode);
    }

    private static long AddOutbound(
        IDataStore store,
        FilledHuFixture fixture,
        DocStatus status,
        long itemId,
        long orderLineId)
    {
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"OUT-{Suffix()}",
            Type = DocType.Outbound,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            ClosedAt = status == DocStatus.Closed ? DateTime.UtcNow : null,
            OrderId = fixture.OrderId,
            OrderRef = fixture.OrderRef
        });
        store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            Qty = 17,
            FromLocationId = fixture.LocationId,
            FromHu = fixture.HuCode
        });
        return docId;
    }

    private static TsdHuView Resolve(IDataStore store, string huCode) =>
        new TsdHuResolverService((ITsdHuResolverStore)store).Resolve(huCode);

    private static void EnsureAtLeastOneLocation(IDataStore store)
    {
        if (store.GetLocations().Count > 0)
        {
            return;
        }

        store.AddLocation(new Location
        {
            Code = "FG",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = true
        });
    }

    private static async Task RunInRollbackTransactionAsync(
        string connectionString,
        Func<IDataStore, Task> work)
    {
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        var exception = await Record.ExceptionAsync(() =>
        {
            store.ExecuteInTransaction(scopedStore =>
            {
                work(scopedStore).GetAwaiter().GetResult();
                throw new RollbackRequestedException();
            });
            return Task.CompletedTask;
        });

        Assert.True(
            exception is RollbackRequestedException,
            exception?.ToString() ?? "Expected rollback transaction marker exception.");
    }

    private static string? ResolvePostgresTestConnectionString()
    {
        foreach (var key in new[]
                 {
                     "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
                     "FLOWSTOCK_POSTGRES_CONNECTION",
                     "POSTGRES_CONNECTION_STRING"
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        const string fallback =
            "Host=127.0.0.1;Port=5432;Database=flowstock;Username=flowstock;Password=flowstock;Pooling=false;Timeout=2;Command Timeout=30";
        try
        {
            var store = new PostgresDataStore(fallback);
            store.Initialize();
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static string Suffix() => DateTime.UtcNow.Ticks.ToString()[^8..];

    private sealed class RollbackRequestedException : Exception;

    private sealed record FilledHuFixture(
        long OrderId,
        string OrderRef,
        long[] OrderLineIds,
        long[] ItemIds,
        double[] Quantities,
        long LocationId,
        string HuCode);
}
