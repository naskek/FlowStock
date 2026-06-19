using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletPostgresRegressionTests
{
    [Fact]
    public void GetFilledProductionPalletQtyByOrderLine_Sql_UsesTypedNullableExcludePalletId()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodIndex = source.IndexOf(
            "public double GetFilledProductionPalletQtyByOrderLine",
            StringComparison.Ordinal);
        Assert.True(methodIndex >= 0);

        var methodEnd = source.IndexOf(
            "public IReadOnlyList<ProductionPallet> GetFilledProductionPalletsByItemAndLocation",
            methodIndex,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodIndex);

        var methodBody = source[methodIndex..methodEnd];
        Assert.Contains("@exclude_pallet_id::bigint IS NULL", methodBody, StringComparison.Ordinal);
        Assert.Contains("pp.id <> @exclude_pallet_id::bigint", methodBody, StringComparison.Ordinal);
        Assert.Contains("WHEN EXISTS (", methodBody, StringComparison.Ordinal);
        Assert.Contains("FROM production_pallet_lines pll", methodBody, StringComparison.Ordinal);
        Assert.Contains("WHEN pp.order_line_id = @order_line_id THEN pp.planned_qty", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(@exclude_pallet_id IS NULL OR pp.id <> @exclude_pallet_id)",
            methodBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlanProductionPallets_Sql_SkipsDocLinesAlreadyLinkedToAnyPallet()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodIndex = source.IndexOf(
            "public IReadOnlyList<ProductionPallet> PlanProductionPallets",
            StringComparison.Ordinal);
        Assert.True(methodIndex >= 0);

        var methodEnd = source.IndexOf(
            "public double GetFilledProductionPalletQtyByOrderLine",
            methodIndex,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodIndex);

        var methodBody = source[methodIndex..methodEnd];
        Assert.Contains("WHERE pp.doc_line_id = dl.id", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDocLinesForProductionPallets_Sql_DoesNotDeleteLinesStillReferencedByCancelledPallets()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodIndex = source.IndexOf(
            "public int RemoveDocLinesForProductionPallets",
            StringComparison.Ordinal);
        Assert.True(methodIndex >= 0);

        var methodEnd = source.IndexOf(
            "public void UpdateProductionPalletHu",
            methodIndex,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodIndex);

        var methodBody = source[methodIndex..methodEnd];
        Assert.Contains("FROM production_pallets pp", methodBody, StringComparison.Ordinal);
        Assert.Contains("WHERE pp.doc_line_id = dl.id", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pp.status <> @cancelled_status", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DetachRemovableProductionPalletPlanForDraftReceiptCancel_Sql_DeletesPlanWithoutNullingForeignKeys()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodIndex = source.IndexOf(
            "public void DetachRemovableProductionPalletPlanForDraftReceiptCancel",
            StringComparison.Ordinal);
        Assert.True(methodIndex >= 0);

        var methodEnd = source.IndexOf(
            "public ProductionPalletPlanCleanupCounts ClearPlannedProductionPalletPlanForOrderLines",
            methodIndex,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodIndex);

        var methodBody = source[methodIndex..methodEnd];
        Assert.Contains("DELETE FROM production_pallet_lines pll", methodBody, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM production_pallets", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("doc_line_id = NULL", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("prd_doc_id = NULL", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableExcludePalletIdParameter_DoesNotFailPostgresTypeInference()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using (var nullCommand = connection.CreateCommand())
        {
            nullCommand.CommandText = @"
SELECT 1
WHERE (@exclude_pallet_id::bigint IS NULL OR @exclude_pallet_id::bigint <> 99);";
            nullCommand.Parameters.AddWithValue("exclude_pallet_id", DBNull.Value);
            nullCommand.ExecuteScalar();
        }

        using (var valueCommand = connection.CreateCommand())
        {
            valueCommand.CommandText = @"
SELECT 1
WHERE (@exclude_pallet_id::bigint IS NULL OR @exclude_pallet_id::bigint <> 99);";
            valueCommand.Parameters.AddWithValue("exclude_pallet_id", 42L);
            valueCommand.ExecuteScalar();
        }
    }

    [Fact]
    public void GetFilledProductionPalletQtyByOrderLine_WithNullableExclude_DoesNotThrowWhenPostgresAvailable()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var store = new PostgresDataStore(connectionString);

        var withoutExclude = store.GetFilledProductionPalletQtyByOrderLine(1, excludePalletId: null);
        var withExclude = store.GetFilledProductionPalletQtyByOrderLine(1, excludePalletId: 1);

        Assert.True(withoutExclude >= 0);
        Assert.True(withExclude >= 0);
    }

    [Fact]
    public void GetProductionFillingReadyOrderIds_ExcludesTerminalOrders_WhenReproDatabaseAvailable()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using (var databaseCommand = connection.CreateCommand())
        {
            databaseCommand.CommandText = "SELECT current_database();";
            if (!string.Equals(databaseCommand.ExecuteScalar()?.ToString(), "flowstock_filling_repro", StringComparison.Ordinal))
            {
                return;
            }
        }

        var token = $"filling-ready-{Guid.NewGuid():N}";
        var orderIds = new List<long>();
        try
        {
            var itemId = InsertReturningId(connection, "INSERT INTO items(name, barcode) VALUES (@name, @token) RETURNING id;", token, token);
            var locationId = InsertReturningId(connection, "INSERT INTO locations(code, name) VALUES (@token, @name) RETURNING id;", token, token);
            foreach (var status in new[] { "ACCEPTED", "SHIPPED", "CANCELLED", "MERGED" })
            {
                var orderId = InsertReturningId(connection,
                    "INSERT INTO orders(order_ref, order_type, status, created_at) VALUES (@token, 'CUSTOMER', @name, @token) RETURNING id;",
                    $"{token}-{status}", status);
                orderIds.Add(orderId);
                var docId = InsertReturningId(connection,
                    "INSERT INTO docs(doc_ref, type, status, created_at, order_id) VALUES (@token, 'PRD', 'CLOSED', @token, @id) RETURNING id;",
                    $"{token}-doc-{status}", null, orderId);
                var docLineId = InsertReturningId(connection,
                    "INSERT INTO doc_lines(doc_id, item_id, qty, to_location_id, to_hu) VALUES (@id, @item_id, 1, @location_id, @token) RETURNING id;",
                    $"{token}-hu-{status}", null, docId, itemId, locationId);
                using var palletCommand = connection.CreateCommand();
                palletCommand.CommandText = @"INSERT INTO production_pallets(prd_doc_id, doc_line_id, order_id, item_id, hu_code, planned_qty, to_location_id, status, created_at)
VALUES (@doc_id, @doc_line_id, @order_id, @item_id, @hu, 1, @location_id, 'FILLED', @created_at);";
                palletCommand.Parameters.AddWithValue("doc_id", docId);
                palletCommand.Parameters.AddWithValue("doc_line_id", docLineId);
                palletCommand.Parameters.AddWithValue("order_id", orderId);
                palletCommand.Parameters.AddWithValue("item_id", itemId);
                palletCommand.Parameters.AddWithValue("hu", $"{token}-hu-{status}");
                palletCommand.Parameters.AddWithValue("location_id", locationId);
                palletCommand.Parameters.AddWithValue("created_at", token);
                palletCommand.ExecuteNonQuery();
            }

            var readyIds = new PostgresDataStore(connectionString).GetProductionFillingReadyOrderIds();

            Assert.Contains(orderIds[0], readyIds);
            Assert.DoesNotContain(orderIds[1], readyIds);
            Assert.DoesNotContain(orderIds[2], readyIds);
            Assert.DoesNotContain(orderIds[3], readyIds);
        }
        finally
        {
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = @"DELETE FROM production_pallets WHERE hu_code LIKE @token;
DELETE FROM doc_lines WHERE to_hu LIKE @token;
DELETE FROM docs WHERE doc_ref LIKE @token;
DELETE FROM orders WHERE order_ref LIKE @token;
DELETE FROM locations WHERE code = @exact_token;
DELETE FROM items WHERE barcode = @exact_token;";
            cleanup.Parameters.AddWithValue("token", token + "%");
            cleanup.Parameters.AddWithValue("exact_token", token);
            cleanup.ExecuteNonQuery();
        }
    }

    private static long InsertReturningId(
        NpgsqlConnection connection,
        string sql,
        string token,
        string? name = null,
        long? id = null,
        long? itemId = null,
        long? locationId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("token", token);
        if (name != null) command.Parameters.AddWithValue("name", name);
        if (id.HasValue) command.Parameters.AddWithValue("id", id.Value);
        if (itemId.HasValue) command.Parameters.AddWithValue("item_id", itemId.Value);
        if (locationId.HasValue) command.Parameters.AddWithValue("location_id", locationId.Value);
        return Convert.ToInt64(command.ExecuteScalar());
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

    private static string GetPostgresDataStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("PostgresDataStore.cs not found from test output directory.");
    }
}
