using FlowStock.Core.Models;
using FlowStock.Data;
using FlowStock.Server.Tests.Tsd;
using Npgsql;

namespace FlowStock.Server.Tests.IncomingRequestsOrderConvergence;

public sealed class OrderRequestIdempotencyPostgresTests
{
    [PostgresFact]
    public async Task ConcurrentRetryOfOneCreateOperation_PersistsExactlyOneRequest()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var key = Guid.NewGuid();
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                Task.Run(() => new PostgresDataStore(connectionString).AddCreateOrderRequest(
                    NewRequest(), key, "hash-a"))));

            Assert.Single(results.Where(result => result.WasCreated));
            Assert.All(results, result =>
            {
                Assert.False(result.IsConflict);
                Assert.Equal(results[0].RequestId, result.RequestId);
                Assert.Equal(OrderRequestStatus.Pending, result.Status);
            });
            Assert.Equal(1, await CountRequestsAsync(connectionString, key));
            var repeated = new PostgresDataStore(connectionString).FindCreateOrderRequest(
                key, "hash-a", "operator", "PC-1");
            Assert.NotNull(repeated);
            Assert.False(repeated!.WasCreated);
            Assert.Equal(results[0].RequestId, repeated.RequestId);
        }
        finally
        {
            await DeleteRequestsAsync(connectionString, key);
        }
    }

    [PostgresFact]
    public async Task SameKeyWithChangedPayloadOrActor_IsConflictWithoutSecondWrite()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var key = Guid.NewGuid();
        try
        {
            var store = new PostgresDataStore(connectionString);
            var created = store.AddCreateOrderRequest(NewRequest(), key, "hash-a");
            Assert.True(created.WasCreated);
            var differentPayload = store.AddCreateOrderRequest(NewRequest(), key, "hash-b");
            Assert.True(differentPayload.IsConflict);
            Assert.False(differentPayload.WasCreated);
            Assert.True(store.FindCreateOrderRequest(key, "hash-a", "other", "PC-1")!.IsConflict);
            Assert.True(store.FindCreateOrderRequest(key, "hash-a", "operator", "OTHER-PC")!.IsConflict);
            Assert.Equal(1, await CountRequestsAsync(connectionString, key));
        }
        finally
        {
            await DeleteRequestsAsync(connectionString, key);
        }
    }

    [PostgresFact]
    public async Task DistinctKeys_AllowDeliberatelyIdenticalOrders()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        try
        {
            var store = new PostgresDataStore(connectionString);
            var first = store.AddCreateOrderRequest(NewRequest(), firstKey, "same");
            var second = store.AddCreateOrderRequest(NewRequest(), secondKey, "same");
            Assert.True(first.WasCreated);
            Assert.True(second.WasCreated);
            Assert.NotEqual(first.RequestId, second.RequestId);
        }
        finally
        {
            await DeleteRequestsAsync(connectionString, firstKey);
            await DeleteRequestsAsync(connectionString, secondKey);
        }
    }

    private static OrderRequest NewRequest() => new()
    {
        RequestType = OrderRequestType.CreateOrder,
        PayloadJson = "{\"order_type\":\"INTERNAL\"}",
        Status = OrderRequestStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        CreatedByLogin = "operator",
        CreatedByDeviceId = "PC-1"
    };

    private static async Task<int> CountRequestsAsync(string connectionString, Guid key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM order_requests WHERE client_request_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task DeleteRequestsAsync(string connectionString, Guid key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM order_requests WHERE client_request_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
    }
}
