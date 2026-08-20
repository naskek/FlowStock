using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public sealed class CatalogAuthorization(
    WpfMachineAuthorization wpfAuthorization,
    IPcWebSessionResolver pcSessions)
{
    public IResult? RequireManageCatalog(HttpRequest request)
    {
        if (wpfAuthorization.IsAuthorized(request))
        {
            return null;
        }

        var identity = pcSessions.Resolve(request);
        if (identity == null)
        {
            return Results.Json(
                new ApiResult(false, "INVALID_SESSION"),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return identity.CanManageCatalog
            ? null
            : Results.Json(
                new ApiResult(false, "MANAGE_CATALOG_REQUIRED"),
                statusCode: StatusCodes.Status403Forbidden);
    }
}
