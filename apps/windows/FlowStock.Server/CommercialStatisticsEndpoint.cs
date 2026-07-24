using System.Globalization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class CommercialStatisticsEndpoint
{
    private static readonly OrderStatus[] DefaultOrderStatuses =
    [
        OrderStatus.Draft,
        OrderStatus.Accepted,
        OrderStatus.InProgress,
        OrderStatus.Shipped
    ];

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/commercial-statistics", Handle);
    }

    private static IResult Handle(HttpRequest request, CommercialStatisticsService service)
    {
        if (!TryParseMode(request.Query["mode"], out var mode)
            || !TryParseGroupBy(request.Query["group_by"], out var groupBy))
        {
            return Invalid("INVALID_STATISTICS_MODE_OR_GROUP", "Некорректный режим или группировка статистики.");
        }

        if (!TryParseDate(request.Query["from"], out var from)
            || !TryParseDate(request.Query["to"], out var to)
            || to < from)
        {
            return Invalid("INVALID_STATISTICS_PERIOD", "Укажите корректный период статистики.");
        }

        DateTime? detailMonth = null;
        var detailMonthRaw = request.Query["detail_month"].ToString();
        if (!string.IsNullOrWhiteSpace(detailMonthRaw))
        {
            if (!DateTime.TryParseExact(
                    detailMonthRaw,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedMonth))
            {
                return Invalid("INVALID_DETAIL_MONTH", "Месяц детализации должен иметь формат YYYY-MM.");
            }
            detailMonth = new DateTime(parsedMonth.Year, parsedMonth.Month, 1);
            if (detailMonth < new DateTime(from.Year, from.Month, 1)
                || detailMonth > new DateTime(to.Year, to.Month, 1))
            {
                return Invalid("DETAIL_MONTH_OUTSIDE_PERIOD", "Месяц детализации находится вне выбранного периода.");
            }
        }

        var statusesRaw = request.Query["statuses"].ToString();
        if (mode == CommercialStatisticsMode.Sales && !string.IsNullOrWhiteSpace(statusesRaw))
        {
            return Invalid(
                "STATUSES_NOT_SUPPORTED_FOR_SALES",
                "Фильтр статусов применяется только в режиме «Заказы».");
        }

        if (!TryParseStatuses(statusesRaw, out var statuses))
        {
            return Invalid("INVALID_ORDER_STATUSES", "Указан неподдерживаемый статус заказа.");
        }

        if (!TryOptionalLong(request.Query["partner_id"], out var partnerId)
            || !TryOptionalLong(request.Query["item_id"], out var itemId))
        {
            return Invalid("INVALID_STATISTICS_FILTER", "Некорректный идентификатор фильтра.");
        }

        var limit = int.TryParse(request.Query["limit"], out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 500)
            : 100;
        var offset = int.TryParse(request.Query["offset"], out var parsedOffset)
            ? Math.Max(parsedOffset, 0)
            : 0;
        var sort = request.Query["sort"].ToString().Trim().ToLowerInvariant();
        if (sort is not ("" or "gross_desc" or "quantity_desc" or "name_asc"))
        {
            return Invalid("INVALID_STATISTICS_SORT", "Некорректная сортировка статистики.");
        }
        if (string.IsNullOrEmpty(sort))
        {
            sort = "gross_desc";
        }

        var query = new CommercialStatisticsQuery(
            mode,
            groupBy,
            from,
            to.AddDays(1),
            detailMonth,
            partnerId,
            itemId,
            NullIfBlank(request.Query["gtin"]),
            NullIfBlank(request.Query["brand"]),
            NullIfBlank(request.Query["volume"]),
            mode == CommercialStatisticsMode.Orders ? statuses : Array.Empty<OrderStatus>(),
            limit,
            offset,
            sort);
        var result = service.Get(query);
        return Results.Ok(new
        {
            mode = mode.ToString().ToLowerInvariant(),
            group_by = GroupByToApi(groupBy),
            period = new
            {
                from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            detail_month = detailMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            summary = MapAmounts(result.Summary),
            monthly = result.Monthly.Select(row => new
            {
                month = row.Month,
                amounts = MapAmounts(row.Amounts)
            }),
            groups = new
            {
                items = result.Groups.Select(row => new
                {
                    key = row.Key,
                    label = row.Label,
                    amounts = MapAmounts(row.Amounts)
                }),
                total_count = result.TotalGroupCount,
                limit,
                offset
            },
            data_quality = new
            {
                missing_price_fact_count = result.DataQuality.MissingPriceFactCount,
                missing_price_quantity = result.DataQuality.MissingPriceQuantity,
                missing_vat_fact_count = result.DataQuality.MissingVatFactCount,
                missing_vat_quantity = result.DataQuality.MissingVatQuantity,
                financially_incomplete_fact_count = result.DataQuality.FinanciallyIncompleteFactCount,
                financially_incomplete_quantity = result.DataQuality.FinanciallyIncompleteQuantity,
                unlinked_sales_fact_count = result.DataQuality.UnlinkedSalesFactCount,
                unlinked_sales_quantity = result.DataQuality.UnlinkedSalesQuantity,
                item_mismatch_sales_fact_count = result.DataQuality.ItemMismatchSalesFactCount,
                item_mismatch_sales_quantity = result.DataQuality.ItemMismatchSalesQuantity,
                is_financially_complete = result.DataQuality.IsFinanciallyComplete
            }
        });
    }

    private static object MapAmounts(CommercialStatisticsAmounts amounts) => new
    {
        order_count = amounts.OrderCount,
        document_count = amounts.DocumentCount,
        fact_count = amounts.FactCount,
        quantity = amounts.Quantity,
        known_financial_quantity = amounts.KnownFinancialQuantity,
        gross = amounts.Gross,
        net = amounts.Net,
        vat = amounts.Vat
    };

    private static bool TryParseMode(string? raw, out CommercialStatisticsMode mode)
    {
        mode = raw?.Trim().ToLowerInvariant() switch
        {
            "orders" => CommercialStatisticsMode.Orders,
            "sales" => CommercialStatisticsMode.Sales,
            _ => (CommercialStatisticsMode)(-1)
        };
        return Enum.IsDefined(mode);
    }

    private static bool TryParseGroupBy(string? raw, out CommercialStatisticsGroupBy groupBy)
    {
        groupBy = raw?.Trim().ToLowerInvariant() switch
        {
            "partner" => CommercialStatisticsGroupBy.Partner,
            "item" or null or "" => CommercialStatisticsGroupBy.Item,
            "gtin" => CommercialStatisticsGroupBy.Gtin,
            "brand" => CommercialStatisticsGroupBy.Brand,
            "volume" => CommercialStatisticsGroupBy.Volume,
            _ => (CommercialStatisticsGroupBy)(-1)
        };
        return Enum.IsDefined(groupBy);
    }

    private static bool TryParseStatuses(string? raw, out IReadOnlyList<OrderStatus> statuses)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            statuses = DefaultOrderStatuses;
            return true;
        }

        var parsed = new List<OrderStatus>();
        foreach (var token in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var status = OrderStatusMapper.StatusFromString(token);
            if (!status.HasValue
                || status.Value is OrderStatus.Cancelled or OrderStatus.Merged)
            {
                statuses = Array.Empty<OrderStatus>();
                return false;
            }
            parsed.Add(status.Value);
        }
        statuses = parsed.Distinct().ToArray();
        return statuses.Count > 0;
    }

    private static bool TryParseDate(string? raw, out DateTime value) =>
        DateTime.TryParseExact(
            raw?.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);

    private static bool TryOptionalLong(string? raw, out long? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }
        if (!long.TryParse(raw, out var parsed) || parsed <= 0)
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GroupByToApi(CommercialStatisticsGroupBy groupBy) =>
        groupBy.ToString().ToLowerInvariant();

    private static IResult Invalid(string code, string message) =>
        Results.BadRequest(new ApiErrorResult(false, code, message));
}
