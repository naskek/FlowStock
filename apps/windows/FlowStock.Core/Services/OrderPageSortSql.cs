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
}
