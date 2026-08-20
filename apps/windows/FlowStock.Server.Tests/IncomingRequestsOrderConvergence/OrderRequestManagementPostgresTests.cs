using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Tests.Tsd;
using Npgsql;

namespace FlowStock.Server.Tests.IncomingRequestsOrderConvergence;

public sealed class OrderRequestManagementPostgresTests
{
    [PostgresFact]
    public async Task ConcurrentSetOrderStatusConfirmations_CancelCanonicalOrderOnce()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var prefix = $"RBAC-STATUS-{Guid.NewGuid():N}";
        var setupStore = new PostgresDataStore(connectionString);
        var orderId = setupStore.AddOrder(new Order
        {
            OrderRef = prefix,
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var requestId = setupStore.AddOrderRequest(new OrderRequest
        {
            RequestType = OrderRequestType.SetOrderStatus,
            PayloadJson = JsonSerializer.Serialize(new { order_id = orderId, status = "CANCELLED" }),
            Status = OrderRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            var first = Task.Run(() => new OrderRequestManagementService(
                    new PostgresDataStore(connectionString),
                    new PartnerRoleResolver(new PostgresDataStore(connectionString)))
                .Confirm(requestId, "PC:first"));
            var second = Task.Run(() => new OrderRequestManagementService(
                    new PostgresDataStore(connectionString),
                    new PartnerRoleResolver(new PostgresDataStore(connectionString)))
                .Confirm(requestId, "PC:second"));

            var results = await Task.WhenAll(first, second);

            Assert.Single(results.Where(result => result.Kind == OrderRequestManagementResultKind.Success));
            Assert.Single(results.Where(result => result.Kind == OrderRequestManagementResultKind.Conflict));
            var request = Assert.Single(setupStore.GetOrderRequests(true).Where(entry => entry.Id == requestId));
            Assert.Equal(OrderRequestStatus.Approved, request.Status);
            Assert.Equal(orderId, request.AppliedOrderId);
            Assert.Contains(request.ResolvedBy, new[] { "PC:first", "PC:second" });
            Assert.Equal(OrderStatus.Cancelled, setupStore.GetOrder(orderId)?.Status);
            Assert.Single(setupStore.GetOrders().Where(order => order.Id == orderId));
        }
        finally
        {
            await CleanupStatusRequestAsync(connectionString, requestId, orderId);
        }
    }

    [PostgresFact]
    public async Task ConcurrentConfirmAndReject_ProduceOneTerminalOutcome()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var prefix = $"RBAC-MIXED-{Guid.NewGuid():N}";
        var setupStore = new PostgresDataStore(connectionString);
        var itemId = new CatalogService(setupStore).CreateItem(
            $"Товар {prefix}", $"{prefix}-BARCODE", null, "шт", null, null, null, null, false);
        var requestId = setupStore.AddOrderRequest(new OrderRequest
        {
            RequestType = OrderRequestType.CreateOrder,
            PayloadJson = JsonSerializer.Serialize(new
            {
                order_ref = prefix,
                order_type = "INTERNAL",
                lines = new[] { new { item_id = itemId, qty_ordered = 1d, production_purpose = "INTERNAL_STOCK" } }
            }),
            Status = OrderRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            var confirm = Task.Run(() => new OrderRequestManagementService(
                    new PostgresDataStore(connectionString), new PartnerRoleResolver(new PostgresDataStore(connectionString)))
                .Confirm(requestId, "PC:admin"));
            var reject = Task.Run(() => new OrderRequestManagementService(
                    new PostgresDataStore(connectionString), new PartnerRoleResolver(new PostgresDataStore(connectionString)))
                .Reject(requestId, "WPF:operator"));

            var results = await Task.WhenAll(confirm, reject);

            Assert.Single(results.Where(result => result.Kind == OrderRequestManagementResultKind.Success));
            Assert.Single(results.Where(result => result.Kind == OrderRequestManagementResultKind.Conflict));
            var request = Assert.Single(setupStore.GetOrderRequests(true).Where(entry => entry.Id == requestId));
            var createdOrders = setupStore.GetOrders().Where(order => order.OrderRef == prefix).ToList();
            if (request.Status == OrderRequestStatus.Approved)
            {
                Assert.Single(createdOrders);
                Assert.NotNull(request.AppliedOrderId);
            }
            else
            {
                Assert.Equal(OrderRequestStatus.Rejected, request.Status);
                Assert.Empty(createdOrders);
                Assert.Null(request.AppliedOrderId);
            }
        }
        finally
        {
            await CleanupAsync(connectionString, requestId, prefix, itemId);
        }
    }

    [PostgresFact]
    public async Task ConcurrentConfirmations_CreateExactlyOneCanonicalOrder()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var prefix = $"RBAC-{Guid.NewGuid():N}";
        var setupStore = new PostgresDataStore(connectionString);
        var itemId = new CatalogService(setupStore).CreateItem(
            $"Товар {prefix}",
            $"{prefix}-BARCODE",
            null,
            "шт",
            null,
            null,
            null,
            null,
            false);
        var requestId = setupStore.AddOrderRequest(new OrderRequest
        {
            RequestType = OrderRequestType.CreateOrder,
            PayloadJson = JsonSerializer.Serialize(new
            {
                order_ref = prefix,
                order_type = "INTERNAL",
                lines = new[] { new { item_id = itemId, qty_ordered = 1d, production_purpose = "INTERNAL_STOCK" } }
            }),
            Status = OrderRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            var first = Task.Run(() => new OrderRequestManagementService(
                    new PostgresDataStore(connectionString),
                    new PartnerRoleResolver(new PostgresDataStore(connectionString)))
                .Confirm(requestId, "PC:first"));
            var second = Task.Run(() => new OrderRequestManagementService(
                    new PostgresDataStore(connectionString),
                    new PartnerRoleResolver(new PostgresDataStore(connectionString)))
                .Confirm(requestId, "PC:second"));

            var results = await Task.WhenAll(first, second);

            Assert.Single(results.Where(result => result.Kind == OrderRequestManagementResultKind.Success));
            Assert.Single(results.Where(result => result.Kind == OrderRequestManagementResultKind.Conflict));
            Assert.Single(setupStore.GetOrders().Where(order => string.Equals(order.OrderRef, prefix, StringComparison.Ordinal)));
            var request = Assert.Single(setupStore.GetOrderRequests(true).Where(entry => entry.Id == requestId));
            Assert.Equal(OrderRequestStatus.Approved, request.Status);
            Assert.NotNull(request.AppliedOrderId);
        }
        finally
        {
            await CleanupAsync(connectionString, requestId, prefix, itemId);
        }
    }

    private static async Task CleanupAsync(string connectionString, long requestId, string orderRef, long itemId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (sql, parameterName, value) in new (string Sql, string ParameterName, object Value)[]
                 {
                     ("DELETE FROM order_lines WHERE order_id IN (SELECT id FROM orders WHERE order_ref = @order_ref);", "@order_ref", orderRef),
                     ("DELETE FROM orders WHERE order_ref = @order_ref;", "@order_ref", orderRef),
                     ("DELETE FROM order_requests WHERE id = @request_id;", "@request_id", requestId),
                     ("DELETE FROM items WHERE id = @item_id;", "@item_id", itemId)
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(parameterName, value);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task CleanupStatusRequestAsync(string connectionString, long requestId, long orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (sql, parameterName, value) in new (string Sql, string ParameterName, object Value)[]
                 {
                     ("DELETE FROM order_requests WHERE id = @request_id;", "@request_id", requestId),
                     ("DELETE FROM order_lines WHERE order_id = @order_id;", "@order_id", orderId),
                     ("DELETE FROM orders WHERE id = @order_id;", "@order_id", orderId)
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(parameterName, value);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
