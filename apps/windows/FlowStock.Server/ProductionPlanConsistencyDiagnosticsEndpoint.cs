using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class ProductionPlanConsistencyDiagnosticsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/diagnostics/production-plan-consistency", HandleGet);
        app.MapPost("/api/diagnostics/production-plan-consistency/repair", HandleRepair);
    }

    private static async Task<IResult> HandleRepair(HttpRequest request, IDataStore store)
    {
        var body = await request.ReadFromJsonAsync<ProductionPlanConsistencyRepairRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Mode))
        {
            return Results.BadRequest(new { ok = false, message = "Укажите mode." });
        }

        var result = new ProductionPlanConsistencyRepairService(store).Repair(body.Mode.Trim(), body.Apply);
        return Results.Ok(new
        {
            ok = result.Ok,
            applied = result.Applied,
            mode = result.Mode,
            validation_errors = result.ValidationErrors,
            steps = result.Steps.Select(step => new
            {
                action = step.Action,
                target = step.Target,
                detail = step.Detail,
                skipped = step.Skipped
            }),
            diagnostics_after = result.DiagnosticsAfter.Select(MapItem)
        });
    }

    private static IResult HandleGet(IDataStore store)
    {
        var items = new ProductionPlanConsistencyDiagnosticsService(store)
            .GetItems()
            .Select(MapItem)
            .ToArray();

        return Results.Ok(new
        {
            ok = true,
            items
        });
    }

    private static object MapItem(ProductionPlanConsistencyDiagnosticItem item)
    {
        return new
        {
            order_id = item.OrderId,
            order_ref = item.OrderRef,
            order_type = item.OrderType,
            order_status = item.OrderStatus,
            item_id = item.ItemId,
            item_name = item.ItemName,
            order_qty = item.OrderQty,
            open_prd_doc_qty = item.OpenPrdDocQty,
            closed_prd_doc_qty = item.ClosedPrdDocQty,
            prd_doc_qty = item.PrdDocQty,
            open_pallet_planned_qty = item.OpenPalletPlannedQty,
            pallet_planned_qty = item.PalletPlannedQty,
            pallet_filled_qty = item.PalletFilledQty,
            ledger_closed_prd_qty = item.LedgerClosedPrdQty,
            ledger_open_prd_qty = item.LedgerOpenPrdQty,
            ledger_prd_qty = item.LedgerPrdQty,
            severity = item.Severity,
            problem_code = item.ProblemCode,
            recommendation = item.Recommendation,
            pallets = item.Pallets.Select(MapPallet).ToArray(),
            prd_docs = item.PrdDocs.Select(MapPrdDoc).ToArray()
        };
    }

    private static object MapPallet(ProductionPlanConsistencyPalletRow row)
    {
        return new
        {
            pallet_id = row.PalletId,
            prd_doc_id = row.PrdDocId,
            prd_doc_ref = row.PrdDocRef,
            doc_line_id = row.DocLineId,
            order_line_id = row.OrderLineId,
            item_id = row.ItemId,
            hu_code = row.HuCode,
            status = row.Status,
            planned_qty = row.PlannedQty,
            filled_qty = row.FilledQty
        };
    }

    private static object MapPrdDoc(ProductionPlanConsistencyPrdDocRow row)
    {
        return new
        {
            doc_id = row.DocId,
            doc_ref = row.DocRef,
            status = row.Status,
            closed_at = row.ClosedAt?.ToString("O"),
            doc_line_id = row.DocLineId,
            order_line_id = row.OrderLineId,
            item_id = row.ItemId,
            qty = row.Qty
        };
    }
}
