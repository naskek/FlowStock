using System.Globalization;

namespace FlowStock.Server;

public static class CommercialPriceListEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/commercial-pricing/report", (
            HttpRequest request,
            IConfiguration configuration,
            CatalogAuthorization authorization,
            PartnerRoleResolver roleResolver) =>
        {
            if (authorization.RequireManageCatalog(request) is { } rejection)
            {
                return rejection;
            }

            var mode = request.Query["mode"].ToString().Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(mode))
            {
                mode = "all";
            }

            if (mode is not ("all" or "partner" or "item")
                || !TryOptionalId(request.Query["partner_id"], out var partnerId)
                || !TryOptionalId(request.Query["item_id"], out var itemId)
                || !TryBoolean(request.Query["active_items_only"], true, out var activeOnly)
                || !TryBoolean(request.Query["include_inactive_overrides"], false, out var inactive)
                || !TryBoolean(request.Query["include_unpriced"], false, out var unpriced)
                || mode == "partner" && !partnerId.HasValue
                || mode == "item" && !itemId.HasValue)
            {
                return Results.BadRequest(new ApiErrorResult(
                    false, "INVALID_PRICING_FILTER", "Выберите корректный режим и фильтры прайс-листа."));
            }

            if (mode == "partner" && !roleResolver.IsCustomer(partnerId!.Value))
            {
                return Results.BadRequest(new ApiErrorResult(
                    false, "PARTNER_IS_SUPPLIER", "Выбранный контрагент не является клиентом."));
            }

            var query = new CommercialPriceListQuery(
                mode, partnerId, itemId,
                request.Query["gtin"], request.Query["q"], request.Query["brand"], request.Query["volume"],
                activeOnly, inactive, unpriced);
            var result = new CommercialPriceListReader(
                CommercialStatisticsAdvancedReader.BuildConnectionString(configuration)).Get(query);

            return Results.Ok(new
            {
                mode = result.Mode,
                total_count = result.TotalCount,
                items = result.Items.Select(row => new
                {
                    row_kind = row.RowKind,
                    partner_id = row.PartnerId,
                    partner_name = row.PartnerName,
                    partner_code = row.PartnerCode,
                    item_id = row.ItemId,
                    item_name = row.ItemName,
                    gtin = row.Gtin,
                    brand = row.Brand,
                    volume = row.Volume,
                    default_price_gross = row.DefaultPriceGross,
                    partner_price_gross = row.PartnerPriceGross,
                    effective_price_gross = row.EffectivePriceGross,
                    price_source = row.PriceSource,
                    vat_rate = row.VatRate,
                    vat_rate_name = row.VatRateName,
                    item_is_active = row.ItemIsActive,
                    partner_price_is_active = row.PartnerPriceIsActive
                })
            });
        });
    }

    private static bool TryOptionalId(string? raw, out long? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return false;
        }
        id = parsed;
        return true;
    }

    private static bool TryBoolean(string? raw, bool defaultValue, out bool value)
    {
        value = defaultValue;
        return string.IsNullOrWhiteSpace(raw) || bool.TryParse(raw, out value);
    }
}
