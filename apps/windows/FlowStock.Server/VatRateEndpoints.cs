using FlowStock.Core.Services;
using Npgsql;

namespace FlowStock.Server;

public static class VatRateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/vat-rates", (HttpRequest request, VatRateService service, CatalogAuthorization authorization) =>
        {
            var rawIncludeInactive = request.Query["include_inactive"].ToString();
            var includeInactive = string.Equals(rawIncludeInactive, "true", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(rawIncludeInactive, "1", StringComparison.OrdinalIgnoreCase);
            if (includeInactive && authorization.RequireManageCatalog(request) is { } rejection)
            {
                return rejection;
            }

            return Results.Ok(service.GetVatRates(includeInactive).Select(vatRate => new
            {
                id = vatRate.Id,
                name = vatRate.Name,
                rate = vatRate.Rate,
                is_active = vatRate.IsActive,
                sort_order = vatRate.SortOrder
            }));
        });

        app.MapPost("/api/vat-rates", (UpsertVatRateRequest request, HttpRequest httpRequest, VatRateService service, CatalogAuthorization authorization) =>
        {
            if (authorization.RequireManageCatalog(httpRequest) is { } rejection)
            {
                return rejection;
            }

            try
            {
                var id = service.CreateVatRate(
                    request.Name ?? string.Empty,
                    request.Rate,
                    request.SortOrder,
                    request.IsActive);
                return Results.Ok(new { ok = true, vat_rate_id = id });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new ApiResult(false, "Ставка НДС с таким именем или значением уже существует."));
            }
        });

        app.MapPost("/api/vat-rates/{id:long}", (long id, UpsertVatRateRequest request, HttpRequest httpRequest, VatRateService service, CatalogAuthorization authorization) =>
        {
            if (authorization.RequireManageCatalog(httpRequest) is { } rejection)
            {
                return rejection;
            }

            try
            {
                service.UpdateVatRate(
                    id,
                    request.Name ?? string.Empty,
                    request.Rate,
                    request.SortOrder,
                    request.IsActive);
                return Results.Ok(new ApiResult(true));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new ApiResult(false, "Ставка НДС с таким именем или значением уже существует."));
            }
        });

        app.MapDelete("/api/vat-rates/{id:long}", (long id, HttpRequest request, VatRateService service, CatalogAuthorization authorization) =>
        {
            if (authorization.RequireManageCatalog(request) is { } rejection)
            {
                return rejection;
            }

            try
            {
                service.DeleteVatRate(id);
                return Results.Ok(new ApiResult(true));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.Conflict(new ApiResult(
                    false,
                    "Нельзя удалить ставку НДС, которая назначена товарам. Ставку можно деактивировать."));
            }
        });
    }
}
