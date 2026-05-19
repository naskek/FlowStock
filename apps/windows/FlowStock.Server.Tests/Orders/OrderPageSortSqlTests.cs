using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderPageSortSqlTests
{
    [Fact]
    public void BuildEffectiveStatusOrderBy_Default_PrioritizesInProgressAcceptedShipped()
    {
        var sql = OrderPageSortSql.BuildEffectiveStatusOrderBy("eo.effective_status", includeCancelledMerged: false);

        Assert.Contains("WHEN 'IN_PROGRESS' THEN 1", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'ACCEPTED' THEN 2", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'SHIPPED' THEN 3", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CANCELLED", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MERGED", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEffectiveStatusOrderBy_WithCancelledMerged_PutsTerminalStatusesFirst()
    {
        var sql = OrderPageSortSql.BuildEffectiveStatusOrderBy("paged_orders.status", includeCancelledMerged: true);

        Assert.Contains("IN ('CANCELLED', 'MERGED') THEN 0", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'CANCELLED' THEN 1", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'MERGED' THEN 2", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'IN_PROGRESS' THEN 3", sql, StringComparison.Ordinal);
    }
}
