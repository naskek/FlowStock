namespace FlowStock.Core.Services;

public static class OrdersPageApiQuery
{
    public static string BuildQueryString(
        bool includeInternal,
        string? search,
        int limit,
        int offset,
        bool includeCancelledMerged)
    {
        var query = new List<string>();
        if (includeInternal)
        {
            query.Add("include_internal=1");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"q={Uri.EscapeDataString(search.Trim())}");
        }

        query.Add($"limit={limit}");
        query.Add($"offset={offset}");
        query.Add(includeCancelledMerged ? "include_cancelled_merged=1" : "include_cancelled_merged=0");
        return string.Join("&", query);
    }

    public static string BuildPath(
        bool includeInternal,
        string? search,
        int limit,
        int offset,
        bool includeCancelledMerged)
    {
        return "/api/orders?" + BuildQueryString(includeInternal, search, limit, offset, includeCancelledMerged);
    }
}
