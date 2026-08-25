using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using Npgsql;
using System.Text.Json.Serialization;

namespace FlowStock.Server;

public static class MarkingCutoverEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marking/cutover/preflight", HandlePreflight);
        app.MapPost("/api/admin/marking/cutover/line-approvals", HandleLineApproval);
        app.MapPost("/api/admin/marking/cutover/subject-approvals", HandleSubjectApproval);
        app.MapPost("/api/admin/marking/cutover/legacy-task-retirements/dry-run", HandleLegacyTaskRetirementDryRun);
        app.MapPost("/api/admin/marking/cutover/legacy-task-retirements/apply", HandleLegacyTaskRetirementApply);
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

    private static IResult HandleLegacyTaskRetirementDryRun(
        MarkingLegacyTaskRetirementDryRunRequest request,
        HttpRequest httpRequest,
        IMarkingLegacyTaskRetirementStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var authorization = AuthorizeAdmin(httpRequest, wpfAuthorization, pcSessions);
        if (authorization.Rejection != null)
        {
            return authorization.Rejection;
        }

        if (request.OrderLineId <= 0
            || request.MarkingOrderId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.PreflightHash))
        {
            return Results.BadRequest(new { error = "order_line_id, marking_order_id and preflight_hash are required" });
        }

        try
        {
            return Results.Ok(ToLegacyTaskRetirementResponse(store.DryRun(
                request.OrderLineId,
                request.MarkingOrderId,
                request.PreflightHash.Trim().ToLowerInvariant(),
                DateTime.UtcNow)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult HandleLegacyTaskRetirementApply(
        MarkingLegacyTaskRetirementApplyRequest request,
        HttpRequest httpRequest,
        IMarkingLegacyTaskRetirementStore store,
        WpfMachineAuthorization wpfAuthorization,
        IPcWebSessionResolver pcSessions)
    {
        var authorization = AuthorizeAdmin(httpRequest, wpfAuthorization, pcSessions);
        if (authorization.Rejection != null)
        {
            return authorization.Rejection;
        }

        if (!string.Equals(request.Confirm, "APPLY", StringComparison.Ordinal)
            || request.OrderLineId <= 0
            || request.MarkingOrderId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.PreflightHash)
            || string.IsNullOrWhiteSpace(request.EligibilityHash)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.BadRequest(new { error = "confirm=APPLY and all retirement identifiers/hashes are required" });
        }

        try
        {
            return Results.Ok(ToLegacyTaskRetirementResponse(store.Apply(
                request.OrderLineId,
                request.MarkingOrderId,
                request.PreflightHash.Trim().ToLowerInvariant(),
                request.EligibilityHash.Trim().ToLowerInvariant(),
                request.IdempotencyKey.Trim(),
                authorization.Actor!,
                DateTime.UtcNow)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return SerializationConflict();
        }
    }

    private static object ToLegacyTaskRetirementResponse(MarkingLegacyTaskRetirementResult result) => new
    {
        mode = result.Mode,
        eligible = result.Eligible,
        applied = result.WasApplied,
        blocker_codes = result.BlockerCodes,
        order_line_id = result.OrderLineId,
        marking_order_id = result.MarkingOrderId,
        target_qty = result.TargetQuantity,
        candidate_reserved_qty = result.CandidateReservedQuantity,
        remaining_task_count = result.RemainingTaskCount,
        remaining_applied_qty = result.RemainingAppliedQuantity,
        remaining_reserved_qty = result.RemainingReservedQuantity,
        remaining_voided_qty = result.RemainingVoidedQuantity,
        preflight_hash_before = result.PreflightHashBefore,
        preflight_hash_after = result.PreflightHashAfter,
        eligibility_hash = result.EligibilityHash,
        resulting_classification = result.ResultingClassification,
        was_already_applied = result.WasAlreadyApplied
    };

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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return SerializationConflict();
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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return SerializationConflict();
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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return SerializationConflict();
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

    private static IResult SerializationConflict() =>
        Results.Conflict(new { error = "MARKING_CUTOVER_SERIALIZATION_CONFLICT" });

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

    private sealed record MarkingLegacyTaskRetirementDryRunRequest(
        [property: JsonPropertyName("order_line_id")] long OrderLineId,
        [property: JsonPropertyName("marking_order_id")] Guid MarkingOrderId,
        [property: JsonPropertyName("preflight_hash")] string PreflightHash);

    private sealed record MarkingLegacyTaskRetirementApplyRequest(
        [property: JsonPropertyName("order_line_id")] long OrderLineId,
        [property: JsonPropertyName("marking_order_id")] Guid MarkingOrderId,
        [property: JsonPropertyName("preflight_hash")] string PreflightHash,
        [property: JsonPropertyName("eligibility_hash")] string EligibilityHash,
        [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
        [property: JsonPropertyName("confirm")] string Confirm);
}
