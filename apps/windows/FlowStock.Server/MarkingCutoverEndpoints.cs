using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class MarkingCutoverEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marking/cutover/preflight", HandlePreflight);
    }

    private static IResult HandlePreflight(IMarkingCutoverPreflightStore store)
    {
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
}
