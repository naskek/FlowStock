using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderPageSortSqlTests
{
    [Fact]
    public void BuildEffectiveStatusOrderBy_Default_PrioritizesDraftThenInProgress()
    {
        var sql = OrderPageSortSql.BuildEffectiveStatusOrderBy("eo.effective_status", includeCancelledMerged: false);

        Assert.Contains("WHEN 'DRAFT' THEN 1", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'IN_PROGRESS' THEN 2", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'ACCEPTED' THEN 3", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'SHIPPED' THEN 4", sql, StringComparison.Ordinal);
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
        Assert.Contains("WHEN 'DRAFT' THEN 3", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN 'IN_PROGRESS' THEN 4", sql, StringComparison.Ordinal);
    }
}
