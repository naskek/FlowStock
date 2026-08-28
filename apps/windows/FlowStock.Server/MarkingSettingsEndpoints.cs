using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;

namespace FlowStock.Server;

public static class MarkingSettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marking/settings", HandleGet);
        app.MapPut("/api/admin/marking/settings", HandlePut);
    }

    private static IResult HandleGet(
        HttpRequest request,
        IDataStore store,
        CatalogAuthorization authorization)
    {
        var rejection = authorization.RequireManageCatalog(request);
        if (rejection != null) return rejection;
        if (store is not IMarkingAggregateStore markingStore) return Results.StatusCode(503);
        return Results.Ok(new { default_reserve_quantity = markingStore.GetDefaultMarkingReserveQuantity() });
    }

    private static IResult HandlePut(
        UpdateMarkingSettingsRequest body,
        HttpRequest request,
        IDataStore store,
        CatalogAuthorization authorization,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var rejection = authorization.RequireManageCatalog(request);
        if (rejection != null) return rejection;
        if (body.DefaultReserveQuantity < 0)
        {
            return Results.BadRequest(new { error = "MARKING_RESERVE_QUANTITY_INVALID", message = "Резерв должен быть целым числом не меньше нуля." });
        }
        if (store is not IMarkingAggregateStore markingStore) return Results.StatusCode(503);
        var actor = wpfAuthorization.IsAuthorized(request)
            ? WpfMachineAuthorization.GetAuditActor(request)
            : pcSessions.Resolve(request) is { } identity
                ? $"PC:{identity.Login}"
                : "server:marking-settings";
        markingStore.SetDefaultMarkingReserveQuantity(
            body.DefaultReserveQuantity,
            actor,
            DateTime.UtcNow);
        return Results.Ok(new { default_reserve_quantity = body.DefaultReserveQuantity });
    }

    private sealed record UpdateMarkingSettingsRequest(
        [property: JsonPropertyName("default_reserve_quantity")] int DefaultReserveQuantity);
}
