using FlowStock.Core.Services;
using FlowStock.Core.Models;

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

    [Fact]
    public void SortOrders_UsesCreatedAtBeforeNumericOrderRefWithinStatus()
    {
        var olderProductionOrder = new Order
        {
            Id = 112,
            OrderRef = "112",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        var newerOrder = new Order
        {
            Id = 117,
            OrderRef = "117",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc)
        };

        var sorted = OrderPageSortSql.SortOrders([olderProductionOrder, newerOrder], includeCancelledMerged: false);

        Assert.Equal(new[] { "117", "112" }, sorted.Select(order => order.OrderRef).ToArray());
    }

    [Fact]
    public void OrderStatusDisplay_InternalDraftIsRealDraftButCustomerDraftStaysUnchanged()
    {
        Assert.Equal("Черновик", OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft, OrderType.Internal));
        Assert.Equal("В работе", OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft, OrderType.Customer));
    }
}
