using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Data;

namespace FlowStock.Server.Tests.Orders;

/// <summary>
/// Регрессия: после расчёта customer readiness из protected coverage SQL внутри
/// <see cref="PostgresDataStore.GetOrder"/> (CTE order_line_metrics) появилась
/// неоднозначная колонка order_line_id (42702: column reference "order_line_id" is ambiguous),
/// из-за чего WPF не мог открыть карточку клиентского заказа. Тест реально выполняет SQL
/// на PostgreSQL и ловит parse/runtime-ошибки, а не только in-memory расчёты.
/// Тест пропускается, если тестовая PostgreSQL недоступна.
/// </summary>
public sealed class GetOrderCustomerReadinessPostgresRegressionTests
{
    [Fact]
    public async Task GetOrder_CustomerOrder_ExecutesReadinessSqlWithoutAmbiguousColumn()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedSingleLineCustomerOrder(scopedStore);

            // Основной упавший путь: карточка клиентского заказа (GetOrder выполняет
            // customer readiness / protected coverage CTE целиком).
            var order = scopedStore.GetOrder(fixture.OrderId);
            Assert.NotNull(order);
            Assert.Equal(fixture.OrderRef, order!.OrderRef);
            Assert.Equal(OrderType.Customer, order.Type);

            // Остальные изменённые readiness SQL-пути: line details/read-model и list-metrics.
            // Достаточно, что запросы выполняются без SQL-ошибки.
            _ = scopedStore.GetOrderLineViews(fixture.OrderId);
            var metricsStore = Assert.IsAssignableFrom<IOptimizedOrderListMetricsStore>(scopedStore);
            var metrics = metricsStore.GetOrderListMetrics([fixture.OrderId]);
            Assert.True(metrics.ContainsKey(fixture.OrderId));

            return Task.CompletedTask;
        });
    }

    private static SingleLineCustomerOrderFixture SeedSingleLineCustomerOrder(IDataStore store)
    {
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var partnerId = store.AddPartner(new Partner
        {
            Name = $"Readiness клиент {suffix}",
            Code = $"T-RDY-{suffix}"
        });

        var itemId = store.AddItem(new Item
        {
            Name = $"Readiness товар {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        var orderRef = $"T-RDY-{suffix[^6..]}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return new SingleLineCustomerOrderFixture(orderId, orderRef);
    }

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

    private sealed record SingleLineCustomerOrderFixture(long OrderId, string OrderRef);

    private sealed class RollbackRequestedException : Exception;
}
