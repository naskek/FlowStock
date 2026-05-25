namespace FlowStock.Server.Tests.Admin;

public sealed class AdminServiceResetMovementsSqlTests
{
    [Fact]
    public void ResetMovementsSql_DeletesOperationChildrenBeforeDocsAndOrders()
    {
        var source = File.ReadAllText(GetAdminServicePath());

        AssertDeleteBefore(source, "DELETE FROM production_pallet_lines;", "DELETE FROM production_pallets;");
        AssertDeleteBefore(source, "DELETE FROM production_pallets;", "DELETE FROM doc_lines;");
        AssertDeleteBefore(source, "DELETE FROM order_receipt_plan_lines;", "DELETE FROM order_lines;");
        AssertDeleteBefore(source, "DELETE FROM warehouse_action_bundles;", "DELETE FROM docs;");
        AssertDeleteBefore(source, "DELETE FROM marking_order", "DELETE FROM orders;");
        Assert.Contains("SET reprint_of_batch_id = NULL", source, StringComparison.Ordinal);
        Assert.Contains("UPDATE km_code", source, StringComparison.Ordinal);
        Assert.Contains("receipt_doc_id = NULL", source, StringComparison.Ordinal);
        Assert.Contains("ship_doc_id = NULL", source, StringComparison.Ordinal);
    }

    private static void AssertDeleteBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Expected SQL fragment not found: {first}");
        Assert.True(secondIndex >= 0, $"Expected SQL fragment not found: {second}");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static string GetAdminServicePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "windows", "FlowStock.App", "Services", "AdminService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("AdminService.cs not found from test output directory.");
    }
}
