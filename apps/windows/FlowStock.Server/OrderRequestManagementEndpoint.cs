using FlowStock.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderRequestManagementEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/requests/{requestId:long}/confirm", (
            long requestId,
            HttpRequest request,
            IDataStore store,
            WpfMachineAuthorization wpfAuthorization,
            IPcWebSessionResolver pcSessions,
            PartnerRoleResolver partnerRoles) =>
            Handle(requestId, request, store, wpfAuthorization, pcSessions, partnerRoles, approve: true));

        app.MapPost("/api/orders/requests/{requestId:long}/reject", (
            long requestId,
            HttpRequest request,
            IDataStore store,
            WpfMachineAuthorization wpfAuthorization,
            IPcWebSessionResolver pcSessions,
            PartnerRoleResolver partnerRoles) =>
            Handle(requestId, request, store, wpfAuthorization, pcSessions, partnerRoles, approve: false));
    }

    private static IResult Handle(
        long requestId,
        HttpRequest request,
        IDataStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions,
        PartnerRoleResolver partnerRoles,
        bool approve)
    {
        string actor;
        if (wpfAuthorization.IsAuthorized(request))
        {
            actor = WpfMachineAuthorization.GetAuditActor(request);
        }
        else
        {
            var identity = pcSessions.Resolve(request);
            if (identity == null)
            {
                return Results.Json(new ApiResult(false, "UNAUTHORIZED"), statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!identity.CanManagePendingRequests)
            {
                return Results.Json(new ApiResult(false, "MANAGE_PENDING_REQUESTS_REQUIRED"), statusCode: StatusCodes.Status403Forbidden);
            }

            actor = $"PC:{identity.Login}";
        }

        var management = new OrderRequestManagementService(store, partnerRoles);
        var result = approve
            ? management.Confirm(requestId, actor)
            : management.Reject(requestId, actor);
        var envelope = new
        {
            ok = result.Kind == OrderRequestManagementResultKind.Success,
            request_id = result.RequestId,
            status = result.Status,
            applied_order_id = result.AppliedOrderId,
            message = result.Message,
            error = result.Kind == OrderRequestManagementResultKind.Success ? null : result.Message
        };

        return result.Kind switch
        {
            OrderRequestManagementResultKind.Success => Results.Ok(envelope),
            OrderRequestManagementResultKind.NotFound => Results.NotFound(envelope),
            OrderRequestManagementResultKind.Conflict => Results.Conflict(envelope),
            OrderRequestManagementResultKind.ValidationFailed => Results.Json(envelope, statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.Json(envelope, statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
