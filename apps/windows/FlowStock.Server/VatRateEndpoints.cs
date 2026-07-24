using FlowStock.Core.Services;
using Npgsql;

namespace FlowStock.Server;

public static class VatRateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/vat-rates", (HttpRequest request, VatRateService service) =>
        {
            var includeInactive = string.Equals(
                request.Query["include_inactive"].ToString(),
                "true",
                StringComparison.OrdinalIgnoreCase);
            return Results.Ok(service.GetVatRates(includeInactive).Select(vatRate => new
            {
                id = vatRate.Id,
                name = vatRate.Name,
                rate = vatRate.Rate,
                is_active = vatRate.IsActive,
                sort_order = vatRate.SortOrder
            }));
        });

        app.MapPost("/api/vat-rates", (UpsertVatRateRequest request, VatRateService service) =>
        {
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

        app.MapPost("/api/vat-rates/{id:long}", (long id, UpsertVatRateRequest request, VatRateService service) =>
        {
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

        app.MapDelete("/api/vat-rates/{id:long}", (long id, VatRateService service) =>
        {
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
