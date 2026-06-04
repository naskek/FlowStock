using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderPageSortSql
{
    public static string BuildEffectiveStatusOrderBy(string statusColumn, bool includeCancelledMerged)
    {
        return includeCancelledMerged
            ? BuildWithCancelledMergedFirst(statusColumn)
            : BuildActiveOnly(statusColumn);
    }

    private static string BuildActiveOnly(string statusColumn) => $@"
CASE {statusColumn}
    WHEN 'DRAFT' THEN 1
    WHEN 'IN_PROGRESS' THEN 2
    WHEN 'ACCEPTED' THEN 3
    WHEN 'SHIPPED' THEN 4
    ELSE 5
END";

    private static string BuildWithCancelledMergedFirst(string statusColumn) => $@"
CASE
    WHEN {statusColumn} IN ('CANCELLED', 'MERGED') THEN 0
    ELSE 1
END,
CASE {statusColumn}
    WHEN 'CANCELLED' THEN 1
    WHEN 'MERGED' THEN 2
    WHEN 'DRAFT' THEN 3
    WHEN 'IN_PROGRESS' THEN 4
    WHEN 'ACCEPTED' THEN 5
    WHEN 'SHIPPED' THEN 6
    ELSE 99
END";

    public static string BuildOrderRefDescendingOrderBy(string orderRefColumn) => $@"
CASE
    WHEN BTRIM(COALESCE({orderRefColumn}, '')) ~ '^\d+$' THEN BTRIM({orderRefColumn})::numeric
    ELSE NULL
END DESC NULLS LAST,
{orderRefColumn} DESC";

    public static IReadOnlyList<Order> SortOrders(IEnumerable<Order> orders, bool includeCancelledMerged)
    {
        return orders
            .Select((order, index) => new { order, index })
            .OrderBy(entry => GetStatusRank(entry.order.Status, includeCancelledMerged))
            .ThenByDescending(entry => entry.order.CreatedAt)
            .ThenByDescending(entry => TryParseNumericOrderRef(entry.order.OrderRef))
            .ThenByDescending(entry => entry.order.OrderRef ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.order)
            .ToArray();
    }

    private static int GetStatusRank(OrderStatus status, bool includeCancelledMerged)
    {
        if (includeCancelledMerged)
        {
            return status switch
            {
                OrderStatus.Cancelled => 1,
                OrderStatus.Merged => 2,
                OrderStatus.Draft => 3,
                OrderStatus.InProgress => 4,
                OrderStatus.Accepted => 5,
                OrderStatus.Shipped => 6,
                _ => 99
            };
        }

        return status switch
        {
            OrderStatus.Draft => 1,
            OrderStatus.InProgress => 2,
            OrderStatus.Accepted => 3,
            OrderStatus.Shipped => 4,
            _ => 5
        };
    }

    private static long? TryParseNumericOrderRef(string? orderRef)
    {
        var trimmed = orderRef?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
               && trimmed.All(char.IsDigit)
               && long.TryParse(trimmed, out var value)
            ? value
            : null;
    }
}
