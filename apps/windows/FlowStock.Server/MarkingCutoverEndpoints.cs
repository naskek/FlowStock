using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using System.Text.Json.Serialization;

namespace FlowStock.Server;

public static class MarkingCutoverEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marking/cutover/preflight", HandlePreflight);
        app.MapPost("/api/admin/marking/cutover/line-approvals", HandleLineApproval);
        app.MapPost("/api/admin/marking/cutover/subject-approvals", HandleSubjectApproval);
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

    private static IResult HandleLineApproval(
        MarkingCutoverLineApprovalRequest request,
        HttpRequest httpRequest,
        IMarkingCutoverApprovalStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var authorization = AuthorizeAdmin(httpRequest, wpfAuthorization, pcSessions);
        if (authorization.Rejection != null)
        {
            return authorization.Rejection;
        }

        try
        {
            var result = store.ApproveMarkingCutoverLine(
                request.OrderLineId,
                request.AllowedSyntheticQuantity,
                request.PreflightHash,
                authorization.Actor!,
                DateTime.UtcNow);
            return Results.Ok(new
            {
                allowlist_id = result.AllowlistId,
                order_line_id = result.OrderLineId,
                allowed_synthetic_qty = result.AllowedQuantity,
                previous_preflight_hash = result.OriginalPreflightHash,
                preflight_hash = result.CurrentPreflightHash,
                was_already_approved = result.WasAlreadyApproved
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult HandleSubjectApproval(
        MarkingCutoverSubjectApprovalRequest request,
        HttpRequest httpRequest,
        IMarkingCutoverApprovalStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var authorization = AuthorizeAdmin(httpRequest, wpfAuthorization, pcSessions);
        if (authorization.Rejection != null)
        {
            return authorization.Rejection;
        }

        try
        {
            var intents = request.Subjects?
                .Select(subject => new MarkingCutoverSubjectApprovalIntent(
                    subject.MarkingSubjectId,
                    subject.ApprovedQuantity))
                .ToArray() ?? [];
            var result = store.ApproveMarkingCutoverSubjects(
                request.AllowlistId,
                intents,
                request.PreflightHash,
                authorization.Actor!,
                DateTime.UtcNow);
            return Results.Ok(new
            {
                allowlist_id = result.AllowlistId,
                preflight_hash = result.CurrentPreflightHash,
                approval_ids = result.ApprovalIds,
                was_already_approved = result.WasAlreadyApproved
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
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

    private sealed record MarkingCutoverLineApprovalRequest(
        [property: JsonPropertyName("order_line_id")] long OrderLineId,
        [property: JsonPropertyName("allowed_synthetic_qty")] int? AllowedSyntheticQuantity,
        [property: JsonPropertyName("preflight_hash")] string PreflightHash);

    private sealed record MarkingCutoverSubjectApprovalRequest(
        [property: JsonPropertyName("allowlist_id")] long AllowlistId,
        [property: JsonPropertyName("subjects")] IReadOnlyList<MarkingCutoverSubjectApprovalRow>? Subjects,
        [property: JsonPropertyName("preflight_hash")] string PreflightHash);

    private sealed record MarkingCutoverSubjectApprovalRow(
        [property: JsonPropertyName("marking_subject_id")] Guid MarkingSubjectId,
        [property: JsonPropertyName("approved_quantity")] decimal ApprovedQuantity);
}
