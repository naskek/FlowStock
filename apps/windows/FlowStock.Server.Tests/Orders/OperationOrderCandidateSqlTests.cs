using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class OperationOrderCandidateSqlTests
{
    [Fact]
    public void BuildOrderScopeSql_ProductionReceipt_OmitsShipmentMetrics()
    {
        var sql = FlowStock.Data.OperationOrderCandidateSql.BuildOrderScopeSql(
            requireCustomerOrders: false,
            requireReceiptRemaining: true,
            requireShipmentRemaining: false);

        Assert.Contains("limited_candidate_ids", sql, StringComparison.Ordinal);
        Assert.Contains("has_receipt_remaining", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped_by_line", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("has_shipment_remaining", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("marking_rollup", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOrderScopeSql_Outbound_UsesShipmentMetricsOnly()
    {
        var sql = FlowStock.Data.OperationOrderCandidateSql.BuildOrderScopeSql(
            requireCustomerOrders: true,
            requireReceiptRemaining: false,
            requireShipmentRemaining: true);

        Assert.Contains("shipped_by_line", sql, StringComparison.Ordinal);
        Assert.Contains("has_shipment_remaining", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("unlinked_produced_by_item", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("has_receipt_remaining", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresDataStore_ProductionReceipt_UsesReceiptRemainingScopeFlags()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());

        Assert.Contains("docType == DocType.ProductionReceipt", source, StringComparison.Ordinal);
        Assert.Contains("requireReceiptRemaining = docType == DocType.ProductionReceipt", source, StringComparison.Ordinal);
        Assert.Contains("requireShipmentRemaining = docType == DocType.Outbound", source, StringComparison.Ordinal);
        Assert.Contains("requireCustomerOrders = docType == DocType.Outbound", source, StringComparison.Ordinal);
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
