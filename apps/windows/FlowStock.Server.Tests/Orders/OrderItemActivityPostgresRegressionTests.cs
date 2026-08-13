using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderItemActivityPostgresRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DeactivationLocksFirst_OrderCreateWaitsThenRejectsInactiveItem()
    {
        await using var fixture = new ActivityFixture(ResolveRequiredPostgresTestConnectionString());
        var orderRef = $"{fixture.Prefix}-DEACT-FIRST";
        var orderApplicationName = $"{fixture.Prefix}-order-waits";

        await using var deactivationConnection = new NpgsqlConnection(fixture.ConnectionString);
        await deactivationConnection.OpenAsync().WaitAsync(TestTimeout);
        await using var deactivationTransaction = await deactivationConnection.BeginTransactionAsync();
        await SetItemInactive(deactivationConnection, deactivationTransaction, fixture.ItemId);

        var orderStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, orderApplicationName));
        var orderTask = Task.Run(() => Record.Exception(() =>
            new OrderService(orderStore).CreateOrder(
                orderRef,
                partnerId: null,
                dueDate: null,
                comment: null,
                lines:
                [
                    new OrderLineView
                    {
                        ItemId = fixture.ItemId,
                        QtyOrdered = 10,
                        ProductionPurpose = ProductionLinePurpose.InternalStock
                    }
                ],
                type: OrderType.Internal)));

        Exception? synchronizationError = null;
        try
        {
            await WaitUntilSessionWaitsForLock(
                fixture.ConnectionString,
                orderApplicationName).WaitAsync(TestTimeout);
            Assert.False(orderTask.IsCompleted);
            await deactivationTransaction.CommitAsync().WaitAsync(TestTimeout);
        }
        catch (Exception ex)
        {
            synchronizationError = ex;
            await deactivationTransaction.RollbackAsync().WaitAsync(TestTimeout);
        }

        var error = Assert.IsType<OrderItemActivityException>(
            await orderTask.WaitAsync(TestTimeout));
        Assert.Null(synchronizationError);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.ErrorCode);

        var state = await fixture.ReadState(orderRef);
        Assert.False(state.ItemIsActive);
        Assert.Equal(0, state.OrderCount);
        Assert.Equal(0, state.LineCount);
        Assert.Equal(0, state.PlanCount);
        Assert.Equal(0, state.ReservationCount);
    }

    [Fact]
    public async Task OrderLockAcquiredFirst_DeactivationWaitsThenCommitsAfterCompleteOrder()
    {
        await using var fixture = new ActivityFixture(ResolveRequiredPostgresTestConnectionString());
        var orderRef = $"{fixture.Prefix}-ORDER-FIRST";
        var deactivationApplicationName = $"{fixture.Prefix}-deactivation-waits";
        var itemLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOrderMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long orderId = 0;

        var orderStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, $"{fixture.Prefix}-order-first"));
        var orderTask = Task.Run(() => Record.Exception(() =>
            orderStore.ExecuteInTransaction(scopedStore =>
            {
                OrderItemActivityGuard.EnsureActiveForAdditionalOrderQuantity(
                    scopedStore,
                    [fixture.ItemId]);
                itemLockAcquired.TrySetResult();
                releaseOrderMutation.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();

                orderId = scopedStore.AddOrder(new Order
                {
                    OrderRef = orderRef,
                    Type = OrderType.Internal,
                    Status = OrderStatus.InProgress,
                    CreatedAt = DateTime.UtcNow
                });
                scopedStore.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = fixture.ItemId,
                    QtyOrdered = 10,
                    ProductionPurpose = ProductionLinePurpose.InternalStock
                });
            })));

        await itemLockAcquired.Task.WaitAsync(TestTimeout);

        var deactivationTask = Task.Run(async () => await Record.ExceptionAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(
                WithApplicationName(fixture.ConnectionString, deactivationApplicationName));
            await connection.OpenAsync().WaitAsync(TestTimeout);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetItemInactive(connection, transaction, fixture.ItemId);
            await transaction.CommitAsync().WaitAsync(TestTimeout);
        }));

        Exception? synchronizationError = null;
        try
        {
            await WaitUntilSessionWaitsForLock(
                fixture.ConnectionString,
                deactivationApplicationName).WaitAsync(TestTimeout);
            Assert.False(deactivationTask.IsCompleted);
        }
        catch (Exception ex)
        {
            synchronizationError = ex;
        }
        finally
        {
            releaseOrderMutation.TrySetResult();
        }

        Assert.Null(await orderTask.WaitAsync(TestTimeout));
        Assert.True(orderId > 0);
        Assert.Null(await deactivationTask.WaitAsync(TestTimeout));
        Assert.Null(synchronizationError);

        var state = await fixture.ReadState(orderRef);
        Assert.False(state.ItemIsActive);
        Assert.Equal(1, state.OrderCount);
        Assert.Equal(1, state.LineCount);
        Assert.Equal(10, state.TotalQty, 3);
        Assert.Equal(0, state.DuplicateLineCount);
        Assert.Equal(0, state.PlanCount);
        Assert.Equal(0, state.ReservationCount);
    }

    [Fact]
    public async Task MultiLineUpdateWithInactiveAddition_LeavesCommittedOrderUnchanged()
    {
        await using var fixture = new ActivityFixture(ResolveRequiredPostgresTestConnectionString());
        var inactiveItemId = fixture.CreateItem("inactive-update");
        var orderRef = $"{fixture.Prefix}-ATOMIC";
        var store = new PostgresDataStore(fixture.ConnectionString);
        var orderId = new OrderService(store).CreateOrder(
            orderRef,
            partnerId: null,
            dueDate: null,
            comment: "before",
            lines:
            [
                new OrderLineView
                {
                    ItemId = fixture.ItemId,
                    QtyOrdered = 10,
                    ProductionPurpose = ProductionLinePurpose.InternalStock
                }
            ],
            type: OrderType.Internal);
        var existingLine = Assert.Single(store.GetOrderLines(orderId));

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync().WaitAsync(TestTimeout);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetItemInactive(connection, transaction, inactiveItemId);
            await transaction.CommitAsync().WaitAsync(TestTimeout);
        }

        var error = Assert.Throws<OrderItemActivityException>(() =>
            new OrderService(store).UpdateOrder(
                orderId,
                $"{orderRef}-CHANGED",
                partnerId: null,
                dueDate: null,
                comment: "after",
                lines:
                [
                    new OrderLineView
                    {
                        Id = existingLine.Id,
                        ItemId = fixture.ItemId,
                        QtyOrdered = 5,
                        ProductionPurpose = ProductionLinePurpose.InternalStock
                    },
                    new OrderLineView
                    {
                        ItemId = inactiveItemId,
                        QtyOrdered = 2,
                        ProductionPurpose = ProductionLinePurpose.InternalStock
                    }
                ],
                type: OrderType.Internal));

        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.ErrorCode);
        var committedOrder = Assert.IsType<Order>(store.GetOrder(orderId));
        Assert.Equal(orderRef, committedOrder.OrderRef);
        Assert.Equal("before", committedOrder.Comment);
        var committedLine = Assert.Single(store.GetOrderLines(orderId));
        Assert.Equal(existingLine.Id, committedLine.Id);
        Assert.Equal(10, committedLine.QtyOrdered, 3);
        Assert.DoesNotContain(store.GetOrderLines(orderId), line => line.ItemId == inactiveItemId);
        Assert.Empty(store.GetOrderReceiptPlanLines(orderId));
    }

    private static async Task SetItemInactive(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long itemId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = "UPDATE items SET is_active = false WHERE id = @item_id;";
        command.Parameters.AddWithValue("@item_id", itemId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync().WaitAsync(TestTimeout));
    }

    private static async Task WaitUntilSessionWaitsForLock(
        string connectionString,
        string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().WaitAsync(TestTimeout);
        var deadline = DateTime.UtcNow.Add(TestTimeout);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = """
SELECT EXISTS (
    SELECT 1
    FROM pg_stat_activity
    WHERE application_name = @application_name
      AND wait_event_type = 'Lock'
);
""";
            command.Parameters.AddWithValue("@application_name", applicationName);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync().WaitAsync(TestTimeout)))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Сессия {applicationName} не перешла в ожидание PostgreSQL lock.");
    }

    private static string WithApplicationName(string connectionString, string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
            CommandTimeout = 5
        };
        return builder.ConnectionString;
    }

    private static string ResolveRequiredPostgresTestConnectionString()
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

        throw new InvalidOperationException(
            "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
    }

    private sealed class ActivityFixture : IAsyncDisposable
    {
        private readonly List<long> _itemIds = [];

        public ActivityFixture(string connectionString)
        {
            ConnectionString = connectionString;
            Prefix = $"OIA-{Guid.NewGuid():N}";
            ItemId = CreateItem("primary");
        }

        public long CreateItem(string suffix)
        {
            var store = new PostgresDataStore(ConnectionString);
            var itemId = new CatalogService(store).CreateItem(
                name: $"Товар {Prefix}",
                barcode: $"{Prefix}-{suffix}-BARCODE",
                gtin: $"{Prefix}-{suffix}-GTIN",
                baseUom: "шт",
                brand: Prefix,
                volume: null,
                shelfLifeMonths: null,
                taraId: null,
                isMarked: false,
                isActive: true);
            _itemIds.Add(itemId);
            return itemId;
        }

        public string ConnectionString { get; }
        public string Prefix { get; }
        public long ItemId { get; }

        public async Task<DatabaseState> ReadState(string orderRef)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync().WaitAsync(TestTimeout);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = """
SELECT i.is_active,
       COUNT(DISTINCT o.id) AS order_count,
       COUNT(ol.id) AS line_count,
       COALESCE(SUM(ol.qty_ordered), 0) AS total_qty,
       GREATEST(COUNT(ol.id) - COUNT(DISTINCT (ol.item_id, COALESCE(ol.production_purpose, ''))), 0) AS duplicate_line_count,
       COUNT(DISTINCT rp.id) AS plan_count,
       COUNT(DISTINCT CASE WHEN rp.to_hu IS NOT NULL AND BTRIM(rp.to_hu) <> '' THEN rp.id END) AS reservation_count
FROM items i
LEFT JOIN orders o ON o.order_ref = @order_ref
LEFT JOIN order_lines ol ON ol.order_id = o.id
LEFT JOIN order_receipt_plan_lines rp ON rp.order_id = o.id
WHERE i.id = @item_id
GROUP BY i.is_active;
""";
            command.Parameters.AddWithValue("@order_ref", orderRef);
            command.Parameters.AddWithValue("@item_id", ItemId);
            await using var reader = await command.ExecuteReaderAsync().WaitAsync(TestTimeout);
            Assert.True(await reader.ReadAsync().WaitAsync(TestTimeout));
            return new DatabaseState(
                reader.GetBoolean(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6));
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync().WaitAsync(TestTimeout);
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 5;
            command.CommandText = """
DELETE FROM business_notifications
WHERE entity_type = 'ORDER'
  AND entity_id IN (SELECT id FROM orders WHERE order_ref LIKE @prefix);
DELETE FROM order_receipt_plan_lines
WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE @prefix);
DELETE FROM order_lines
WHERE order_id IN (SELECT id FROM orders WHERE order_ref LIKE @prefix);
DELETE FROM orders WHERE order_ref LIKE @prefix;
DELETE FROM items WHERE id = ANY(@item_ids);
""";
            command.Parameters.AddWithValue("@prefix", $"{Prefix}%");
            command.Parameters.AddWithValue("@item_ids", _itemIds.ToArray());
            await command.ExecuteNonQueryAsync().WaitAsync(TestTimeout);
            await transaction.CommitAsync().WaitAsync(TestTimeout);
        }
    }

    private sealed record DatabaseState(
        bool ItemIsActive,
        long OrderCount,
        long LineCount,
        double TotalQty,
        long DuplicateLineCount,
        long PlanCount,
        long ReservationCount);
}
