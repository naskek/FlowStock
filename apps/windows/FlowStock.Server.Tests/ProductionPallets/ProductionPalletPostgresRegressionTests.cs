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
