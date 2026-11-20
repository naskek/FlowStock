using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.UpdateOrder;

public sealed class PostgresOrderRefLookupRegressionTests
{
    [Fact]
    public void ReferenceLookup_ExcludesCurrentOrder_ButIncludesTerminalOrdersAndPreservesRawRefs()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var token = $"order-ref-perf-{Guid.NewGuid():N}";
        var insertedIds = new List<long>();
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        long Insert(string orderRef, string status)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO orders (order_ref, order_type, status, created_at)
VALUES (@order_ref, 'CUSTOMER', @status, @created_at)
RETURNING id;";
            command.Parameters.AddWithValue("@order_ref", orderRef);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@created_at", "2042-01-01T00:00:00");
            var id = Convert.ToInt64(command.ExecuteScalar());
            insertedIds.Add(id);
            return id;
        }

        try
        {
            var currentRef = $"{token}-Current";
            var cancelledRef = $"{token}-Cancelled";
            var mergedRef = $"{token}-Merged";
            var numericRef = "000123450000";
            var currentId = Insert(currentRef, "ACCEPTED");
            var otherId = Insert($"{token}-Other", "DRAFT");
            Insert(cancelledRef, "CANCELLED");
            Insert(mergedRef, "MERGED");
            var numericId = Insert($"{token}-{numericRef}", "ACCEPTED");

            var store = new PostgresDataStore(connectionString);

            // A PUT with the same ref as its own persisted order must not be renamed.
            Assert.False(store.HasOtherOrderWithRef(currentId, currentRef));
            Assert.True(store.HasOtherOrderWithRef(otherId, currentRef.ToUpperInvariant()));
            Assert.True(store.HasOtherOrderWithRef(currentId, cancelledRef.ToLowerInvariant()));
            Assert.True(store.HasOtherOrderWithRef(currentId, mergedRef));
            Assert.False(store.HasOtherOrderWithRef(currentId, $"{token}-Missing"));
            Assert.False(store.HasOtherOrderWithRef(numericId, $"{token}-{numericRef}"));

            // Auto-number fallback uses raw refs, not a filtered active-order model.
            var refs = store.GetAllOrderRefsForNextNumber();
            Assert.Contains(currentRef, refs);
            Assert.Contains(cancelledRef, refs);
            Assert.Contains(mergedRef, refs);
            Assert.Contains($"{token}-{numericRef}", refs);
        }
        finally
        {
            if (insertedIds.Count > 0)
            {
                using var cleanup = connection.CreateCommand();
                cleanup.CommandText = "DELETE FROM orders WHERE id = ANY(@ids);";
                cleanup.Parameters.AddWithValue("@ids", insertedIds.ToArray());
                cleanup.ExecuteNonQuery();
            }
        }
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

        return null;
    }
}
