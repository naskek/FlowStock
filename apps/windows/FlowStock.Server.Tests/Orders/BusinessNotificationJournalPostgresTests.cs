using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Data;
using FlowStock.Server.Tests.Tsd;

namespace FlowStock.Server.Tests.Orders;

public sealed class BusinessNotificationJournalPostgresTests
{
    [PostgresFact]
    public void StatusTransitions_AreAtomicReplaySafeAndKeepHistoricalClient()
    {
        Rollback(store =>
        {
            var reference = "EVENT-" + Guid.NewGuid().ToString("N");
            var partnerId = store.AddPartner(new Partner { Name = "Клиент до переименования", CreatedAt = DateTime.UtcNow });
            var id = store.AddOrder(new Order { OrderRef = reference, Type = OrderType.Customer, PartnerId = partnerId, Status = OrderStatus.InProgress, CreatedAt = DateTime.UtcNow });
            var journal = (IBusinessNotificationJournalStore)store;
            var query = new BusinessNotificationQuery(false, Search: reference);
            store.UpdateOrderStatus(id, OrderStatus.Accepted);
            store.UpdateOrderStatus(id, OrderStatus.Accepted);
            var first = Assert.Single(journal.GetBusinessNotificationPage(query, reference).Items);
            Assert.Equal("CUSTOMER_ORDER_READY", first.EventType);
            Assert.Equal("Клиент до переименования", first.Payload!.PartnerName);
            store.UpdatePartner(new Partner { Id = partnerId, Name = "Новое имя", CreatedAt = DateTime.UtcNow });
            Assert.Equal("Клиент до переименования", Assert.Single(journal.GetBusinessNotificationPage(query, reference).Items).Payload!.PartnerName);
            store.UpdateOrderStatus(id, OrderStatus.InProgress);
            store.UpdateOrderStatus(id, OrderStatus.Accepted);
            store.UpdateOrderStatus(id, OrderStatus.Shipped);
            var rows = journal.GetBusinessNotificationPage(query, reference).Items;
            Assert.Equal(3, rows.Count);
            Assert.Equal(3, rows.Select(n => n.DedupeKey).Distinct().Count());
            Assert.Equal("Новое имя", rows[0].Payload!.PartnerName);
            Assert.Equal("CUSTOMER_ORDER_SHIPPED", rows[0].EventType);
            Assert.False(store.GetOrder(id)!.AllowPartialOutbound);
        });
    }

    [PostgresFact]
    public void Journal_PaginatesSearchesFiltersAndGroupsOnlyProvenShipmentPair()
    {
        Rollback(store =>
        {
            var key = "JOURNAL-" + Guid.NewGuid().ToString("N");
            var journal = (IBusinessNotificationJournalStore)store;
            var now = DateTime.UtcNow;
            for (var i = 0; i < 120; i++)
                store.AddBusinessNotification(Notification(key, i, now.AddMinutes(-i)));
            var query = new BusinessNotificationQuery(false, 50, 0, key);
            var first = journal.GetBusinessNotificationPage(query, key);
            Assert.Equal(120, first.Total);
            Assert.Equal(50, first.Items.Count);
            Assert.True(first.UnreadCount >= 120);
            var second = journal.GetBusinessNotificationPage(query with { Offset = 50 }, key);
            Assert.DoesNotContain(second.Items, n => first.Items.Any(p => p.Id == n.Id));
            Assert.Equal(20, journal.GetBusinessNotificationPage(query with { Offset = 100 }, key).Items.Count);
            Assert.Empty(journal.GetBusinessNotificationPage(query with { Offset = 150 }, key).Items);
            Assert.Equal(10, journal.GetBusinessNotificationPage(query with { FromUtc = now.AddMinutes(-9), ToUtc = now.AddSeconds(1) }, key).Total);
            Assert.Equal(0, journal.GetBusinessNotificationPage(query with { Category = "ERROR" }, key).Total);
            Assert.Equal(120, journal.GetBusinessNotificationPage(query with { Search = "Имя " + key }, key).Total);
            store.MarkBusinessNotificationRead(first.Items[0].Id, key, now);
            Assert.Equal(119, journal.GetBusinessNotificationPage(query with { UnreadOnly = true }, key).Total);

            var pairKey = key + "PAIR";
            var payload = new BusinessNotificationPayload { OrderId = 913, OrderRef = pairKey, DocId = 501, DocRef = "OUT-501" };
            store.AddBusinessNotification(new BusinessNotification { EventType = "OUTBOUND_CLOSED", EntityType = "DOC", EntityRef = "OUT-501", CreatedAt = now, DedupeKey = pairKey + "doc", Payload = payload });
            store.AddBusinessNotification(new BusinessNotification { EventType = "CUSTOMER_ORDER_SHIPPED", EntityType = "ORDER", EntityRef = pairKey, CreatedAt = now, DedupeKey = pairKey + "order", Payload = payload with { DocId = null, DocRef = null } });
            var pair = Assert.Single(journal.GetBusinessNotificationPage(query with { Search = pairKey }, key).Items);
            Assert.Equal("CUSTOMER_ORDER_SHIPPED", pair.EventType);
            Assert.Equal("OUT-501", pair.Payload!.DocRef);
            store.MarkBusinessNotificationRead(pair.Id, key, now);
            Assert.Empty(journal.GetBusinessNotificationPage(query with { Search = pairKey, UnreadOnly = true }, key).Items);
            Assert.Single(journal.GetBusinessNotificationPage(query with { Search = pairKey, UnreadOnly = true }, key + "other-reader").Items);
        });
    }

    [PostgresFact]
    public void RolledBackTransition_DoesNotLeaveNotification()
    {
        var reference = "ROLLBACK-EVENT-" + Guid.NewGuid().ToString("N");
        Rollback(store =>
        {
            var id = store.AddOrder(new Order { OrderRef = reference, Type = OrderType.Internal, Status = OrderStatus.InProgress, CreatedAt = DateTime.UtcNow });
            store.UpdateOrderStatus(id, OrderStatus.Shipped);
            Assert.Single(((IBusinessNotificationJournalStore)store).GetBusinessNotificationPage(new(false, Search: reference), reference).Items);
        });
        var store = new PostgresDataStore(TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!);
        Assert.Empty(store.GetBusinessNotificationPage(new(false, Search: reference), reference).Items);
    }

    [PostgresFact]
    public void LegacyRowsAndAmbiguousCorrelations_AreNotInventedOrDiscarded()
    {
        Rollback(store =>
        {
            var key = Guid.NewGuid().ToString("N");
            store.AddBusinessNotification(new BusinessNotification { EventType = "CUSTOMER_ORDER_READY", EntityType = "ORDER", EntityRef = key, CreatedAt = DateTime.UtcNow, DedupeKey = key });
            var journal = (IBusinessNotificationJournalStore)store;
            Assert.Null(Assert.Single(journal.GetBusinessNotificationPage(new(false, Search: key), key).Items).Payload);
            var payload = new BusinessNotificationPayload { OrderId = 914, OrderRef = key };
            foreach (var i in new[] { 1, 2 })
                store.AddBusinessNotification(new BusinessNotification { EventType = "OUTBOUND_CLOSED", CreatedAt = DateTime.UtcNow, DedupeKey = key + i, Payload = payload with { DocId = i, DocRef = "OUT-" + i } });
            store.AddBusinessNotification(new BusinessNotification { EventType = "CUSTOMER_ORDER_SHIPPED", CreatedAt = DateTime.UtcNow, DedupeKey = key + "status", Payload = payload });
            Assert.Equal(4, journal.GetBusinessNotificationPage(new(false, Search: key), key).Total);
        });
    }

    private static BusinessNotification Notification(string key, int i, DateTime time) => new()
    {
        EventType = "CUSTOMER_ORDER_READY", CreatedAt = time, EntityType = "ORDER", EntityRef = key + i,
        DedupeKey = key + i, Payload = new() { OrderRef = key + i, PartnerName = "Имя " + key }
    };

    private static void Rollback(Action<IDataStore> work)
    {
        var store = new PostgresDataStore(TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!);
        store.Initialize();
        Assert.Throws<RollbackRequested>(() => store.ExecuteInTransaction(scoped => { work(scoped); throw new RollbackRequested(); }));
    }
    private sealed class RollbackRequested : Exception;
}
