using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using System.Text.Json.Serialization;

namespace FlowStock.Server;

public static class MarkingCutoverEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marking/cutover/preflight", HandlePreflight);
        app.MapPost("/api/admin/marking/cutover/enforce", HandleEnforce);
    }

    private static IResult HandlePreflight(
        HttpRequest request,
        IMarkingCutoverPreflightStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var authorization = AuthorizeAdmin(request, wpfAuthorization, pcSessions);
        if (authorization.Rejection != null)
        {
            return authorization.Rejection;
        }

        var result = new MarkingCutoverPreflightService(store).Run(DateTime.UtcNow);
        return Results.Ok(new
        {
            generated_at = result.GeneratedAt,
            preflight_hash = result.Hash,
            canonical_json = result.CanonicalJson,
            issue_count = result.Entries.Count,
            entries = result.Entries.Select(entry => new
            {
                order_id = entry.OrderId,
                order_line_id = entry.OrderLineId,
                issue_code = entry.IssueCode,
                level = entry.Level,
                target_qty = entry.TargetQty,
                real_code_qty = entry.RealCodeQty,
                legacy_synthetic_qty = entry.LegacySyntheticQty,
                details = entry.Details,
                suggested_remediation = entry.SuggestedRemediation
            })
        });
    }

    private static IResult HandleEnforce(
        MarkingCutoverEnforceRequest request,
        HttpRequest httpRequest,
        IMarkingCutoverPreflightStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var authorization = AuthorizeAdmin(httpRequest, wpfAuthorization, pcSessions);
        if (authorization.Rejection != null)
        {
            return authorization.Rejection;
        }

        if (string.IsNullOrWhiteSpace(request.PreflightHash))
        {
            return Results.BadRequest(new { error = "preflight_hash is required" });
        }

        try
        {
            store.EnforceMarkingCutover(
                request.PreflightHash.Trim().ToLowerInvariant(),
                authorization.Actor!,
                DateTime.UtcNow);
            return Results.Ok(new { state = "ENFORCED", preflight_hash = request.PreflightHash.Trim().ToLowerInvariant() });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static (string? Actor, IResult? Rejection) AuthorizeAdmin(
        HttpRequest request,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        if (wpfAuthorization.IsAuthorized(request))
        {
            return (WpfMachineAuthorization.GetAuditActor(request), null);
        }

        var identity = pcSessions.Resolve(request);
        if (identity == null)
        {
            return (null, Results.Json(
                new { error = "INVALID_SESSION" },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        if (!identity.CanManageCatalog)
        {
            return (null, Results.Json(
                new { error = "ADMIN_REQUIRED" },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return ($"PC:{identity.Login}", null);
    }

    private sealed record MarkingCutoverEnforceRequest(
        [property: JsonPropertyName("preflight_hash")] string PreflightHash);
}
