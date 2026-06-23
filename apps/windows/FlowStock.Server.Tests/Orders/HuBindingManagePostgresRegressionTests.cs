using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Data;

namespace FlowStock.Server.Tests.Orders;

/// <summary>
/// Реальные PostgreSQL-проверки нового read SQL экрана управления привязками HU:
/// выборки выполняются без SQL ambiguity, фильтруют по товару, возвращают свободный и
/// привязанный HU, а целевые строки содержат полный набор привязанных HU.
/// Тест пропускается, если тестовая PostgreSQL недоступна.
/// </summary>
public sealed class HuBindingManagePostgresRegressionTests
{
    [Fact]
    public async Task ManagementReadSql_ExecutesAndReturnsFreeAndBoundHu()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var readStore = Assert.IsAssignableFrom<IHuBindingManagementReadStore>(scopedStore);
            var fixture = SeedFixture(scopedStore);

            // Список товаров со складскими HU.
            var items = readStore.GetManagementItems(null, 100);
            Assert.Contains(items, item => item.ItemId == fixture.ItemId);

            // HU выбранного товара: свободный и привязанный, без SQL ambiguity, с фильтром по товару.
            var page = readStore.GetManagementHuRows(fixture.ItemId, new HuBindingManageHuFilter { Limit = 100 });
            Assert.All(page.HuRows, row => Assert.Equal(fixture.ItemId, row.ItemId));
            var free = Assert.Single(page.HuRows, row => row.HuCode == "HU-FREE");
            Assert.Equal("FREE", free.State);
            var bound = Assert.Single(page.HuRows, row => row.HuCode == "HU-BOUND");
            Assert.Equal("BOUND", bound.State);
            Assert.NotNull(bound.CurrentAssignment);
            Assert.Equal(fixture.OrderId, bound.CurrentAssignment!.OrderId);

            // Фильтр по другому товару не возвращает HU нашего товара.
            var otherItemPage = readStore.GetManagementHuRows(fixture.OtherItemId, new HuBindingManageHuFilter { Limit = 100 });
            Assert.DoesNotContain(otherItemPage.HuRows, row => row.HuCode == "HU-FREE" || row.HuCode == "HU-BOUND");

            // Целевые строки содержат полный набор привязанных HU.
            var targets = readStore.GetManagementTargetLines(fixture.ItemId);
            var target = Assert.Single(targets, line => line.OrderLineId == fixture.OrderLineId);
            Assert.Contains("HU-BOUND", target.CurrentBoundHuCodes);

            return Task.CompletedTask;
        });
    }

    private static ManageFixture SeedFixture(IDataStore store)
    {
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var locationId = store.AddLocation(new Location { Code = $"MNG-{suffix[^6..]}", Name = "Управление HU" });
        var partnerId = store.AddPartner(new Partner { Name = $"Клиент {suffix}", Code = $"T-MNG-{suffix}" });
        var itemId = store.AddItem(new Item { Name = $"Товар MNG {suffix}", BaseUom = "шт", MaxQtyPerHu = 600 });
        var otherItemId = store.AddItem(new Item { Name = $"Товар MNG-2 {suffix}", BaseUom = "шт", MaxQtyPerHu = 600 });

        var orderId = store.AddOrder(new Order
        {
            OrderRef = $"SO-MNG-{suffix[^6..]}",
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PRD-MNG-{suffix[^6..]}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.UtcNow
        });

        store.AddLedgerEntry(new LedgerEntry { Timestamp = DateTime.UtcNow, DocId = docId, ItemId = itemId, LocationId = locationId, QtyDelta = 100, HuCode = "HU-FREE" });
        store.AddLedgerEntry(new LedgerEntry { Timestamp = DateTime.UtcNow, DocId = docId, ItemId = itemId, LocationId = locationId, QtyDelta = 600, HuCode = "HU-BOUND" });

        store.ReplaceOrderReceiptPlanLines(orderId, new[]
        {
            new OrderReceiptPlanLine
            {
                OrderId = orderId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                QtyPlanned = 600,
                ToHu = "HU-BOUND",
                ToLocationId = locationId
            }
        });

        return new ManageFixture(itemId, otherItemId, orderId, orderLineId);
    }

    private static async Task RunInRollbackTransactionAsync(string connectionString, Func<IDataStore, Task> work)
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

    private sealed record ManageFixture(long ItemId, long OtherItemId, long OrderId, long OrderLineId);

    private sealed class RollbackRequestedException : Exception;
}
