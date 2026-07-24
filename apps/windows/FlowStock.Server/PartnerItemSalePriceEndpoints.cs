using FlowStock.Core.Services;
using Npgsql;

namespace FlowStock.Server;

public static class PartnerItemSalePriceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/partner-item-sale-prices", HandleList);
        app.MapPost("/api/partner-item-sale-prices", HandleCreate);
        app.MapPost("/api/partner-item-sale-prices/{id:long}", HandleUpdate);
        app.MapDelete("/api/partner-item-sale-prices/{id:long}", HandleDelete);
    }

    private static IResult HandleList(
        HttpRequest request,
        PartnerItemSalePriceService service)
    {
        if (!TryOptionalLong(request.Query["partner_id"], out var partnerId)
            || !TryOptionalLong(request.Query["item_id"], out var itemId)
            || !TryOptionalBool(request.Query["is_active"], out var isActive))
        {
            return Results.BadRequest(new ApiErrorResult(false, "INVALID_FILTER", "Некорректный фильтр цен клиентов."));
        }

        var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? parsedLimit : 100;
        var offset = int.TryParse(request.Query["offset"], out var parsedOffset) ? parsedOffset : 0;
        var page = service.Get(
            partnerId,
            itemId,
            isActive,
            request.Query["q"],
            limit,
            offset);

        return Results.Ok(new
        {
            items = page.Items.Select(row => new
            {
                id = row.Id,
                partner_id = row.PartnerId,
                partner_name = row.PartnerName,
                partner_code = row.PartnerCode,
                item_id = row.ItemId,
                item_name = row.ItemName,
                unit_price_gross = row.UnitPriceGross,
                is_active = row.IsActive
            }),
            total_count = page.TotalCount,
            limit = page.Limit,
            offset = page.Offset
        });
    }

    private static IResult HandleCreate(
        UpsertPartnerItemSalePriceRequest request,
        PartnerItemSalePriceService service,
        PartnerRoleResolver roleResolver)
    {
        if (!roleResolver.IsCustomer(request.PartnerId))
        {
            return Results.BadRequest(new ApiErrorResult(
                false,
                "PARTNER_IS_SUPPLIER",
                "Индивидуальная цена может быть задана только для клиента."));
        }

        try
        {
            var id = service.Create(
                request.PartnerId,
                request.ItemId,
                request.UnitPriceGross,
                request.IsActive);
            return Results.Ok(new { ok = true, partner_item_sale_price_id = id });
        }
        catch (CommercialTermsException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new ApiErrorResult(
                false,
                "PARTNER_ITEM_SALE_PRICE_DUPLICATE",
                "Для этой пары клиент–товар цена уже существует."));
        }
    }

    private static IResult HandleUpdate(
        long id,
        UpsertPartnerItemSalePriceRequest request,
        PartnerItemSalePriceService service,
        PartnerRoleResolver roleResolver)
    {
        if (!roleResolver.IsCustomer(request.PartnerId))
        {
            return Results.BadRequest(new ApiErrorResult(
                false,
                "PARTNER_IS_SUPPLIER",
                "Индивидуальная цена может быть задана только для клиента."));
        }

        try
        {
            service.Update(
                id,
                request.PartnerId,
                request.ItemId,
                request.UnitPriceGross,
                request.IsActive);
            return Results.Ok(new ApiResult(true));
        }
        catch (CommercialTermsException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new ApiErrorResult(
                false,
                "PARTNER_ITEM_SALE_PRICE_DUPLICATE",
                "Для этой пары клиент–товар цена уже существует."));
        }
    }

    private static IResult HandleDelete(long id, PartnerItemSalePriceService service)
    {
        try
        {
            service.Delete(id);
            return Results.Ok(new ApiResult(true));
        }
        catch (CommercialTermsException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
        }
    }

    private static bool TryOptionalLong(string? value, out long? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryOptionalBool(string? value, out bool? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }
}
