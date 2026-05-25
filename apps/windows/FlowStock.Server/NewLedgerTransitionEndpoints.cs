using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class NewLedgerTransitionEndpoints
{
    private static readonly SemaphoreSlim ApplyLock = new(1, 1);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/admin/maintenance/new-ledger-transition/dry-run", HandleDryRun);
        app.MapPost("/api/admin/maintenance/new-ledger-transition/apply", HandleApply);
    }

    private static IResult HandleDryRun(IDataStore store)
    {
        try
        {
            var report = new NewLedgerTransitionService(store).DryRun();
            return Results.Ok(MapReport("dry-run", report));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }
    }

    private static async Task<IResult> HandleApply(NewLedgerTransitionApplyRequest request, IDataStore store)
    {
        if (!string.Equals(request.Confirm, "APPLY", StringComparison.Ordinal))
        {
            return Results.BadRequest(new ApiResult(false, "CONFIRM_APPLY_REQUIRED"));
        }

        await ApplyLock.WaitAsync();
        try
        {
            var report = new NewLedgerTransitionService(store).Apply();
            return Results.Ok(MapReport("apply", report));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    private static NewLedgerTransitionResponse MapReport(string mode, NewLedgerTransitionReport report) =>
        new(
            true,
            mode,
            report.Applied,
            report.LedgerRowsBefore,
            report.LedgerRowsAfter,
            report.StaleReservationCount,
            report.StaleReservationQty,
            report.StaleReservations.Select(MapStaleReservation).ToArray(),
            report.FilledPalletsWithoutLedger.Select(MapFilledPallet).ToArray(),
            report.DraftPrdsWithLedger.Select(MapDraftPrd).ToArray(),
            report.PlannedActions.Select(MapAction).ToArray());

    private static NewLedgerStaleReservationResponse MapStaleReservation(NewLedgerStaleReservation row) =>
        new(
            row.PlanLineId,
            row.OrderId,
            row.OrderRef,
            row.OrderLineId,
            row.ItemId,
            row.ToHu,
            row.Qty,
            row.CurrentBalance);

    private static NewLedgerFilledPalletDiagnosticResponse MapFilledPallet(NewLedgerFilledPalletDiagnostic row) =>
        new(
            row.ProductionPalletId,
            row.PrdDocId,
            row.PrdDocRef,
            row.OrderId,
            row.OrderLineId,
            row.ItemId,
            row.HuCode,
            row.PlannedQty,
            row.CurrentBalance);

    private static NewLedgerDraftPrdLedgerDiagnosticResponse MapDraftPrd(NewLedgerDraftPrdLedgerDiagnostic row) =>
        new(row.PrdDocId, row.PrdDocRef, row.OrderId, row.LedgerRowCount);

    private static NewLedgerTransitionActionResponse MapAction(NewLedgerTransitionAction row) =>
        new(row.ActionCode, row.OrderId, row.OrderRef, row.OrderLineId, row.ItemId, row.HuCode, row.Details);

    private sealed class NewLedgerTransitionApplyRequest
    {
        [JsonPropertyName("confirm")]
        public string? Confirm { get; init; }
    }

    private sealed record NewLedgerTransitionResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("applied")] bool Applied,
        [property: JsonPropertyName("ledger_rows_before")] long LedgerRowsBefore,
        [property: JsonPropertyName("ledger_rows_after")] long LedgerRowsAfter,
        [property: JsonPropertyName("stale_reservation_count")] int StaleReservationCount,
        [property: JsonPropertyName("stale_reservation_qty")] double StaleReservationQty,
        [property: JsonPropertyName("stale_reservations")] IReadOnlyList<NewLedgerStaleReservationResponse> StaleReservations,
        [property: JsonPropertyName("filled_pallets_without_ledger")] IReadOnlyList<NewLedgerFilledPalletDiagnosticResponse> FilledPalletsWithoutLedger,
        [property: JsonPropertyName("draft_prds_with_ledger")] IReadOnlyList<NewLedgerDraftPrdLedgerDiagnosticResponse> DraftPrdsWithLedger,
        [property: JsonPropertyName("planned_actions")] IReadOnlyList<NewLedgerTransitionActionResponse> PlannedActions);

    private sealed record NewLedgerStaleReservationResponse(
        [property: JsonPropertyName("plan_line_id")] long PlanLineId,
        [property: JsonPropertyName("order_id")] long OrderId,
        [property: JsonPropertyName("order_ref")] string OrderRef,
        [property: JsonPropertyName("order_line_id")] long OrderLineId,
        [property: JsonPropertyName("item_id")] long ItemId,
        [property: JsonPropertyName("to_hu")] string ToHu,
        [property: JsonPropertyName("qty")] double Qty,
        [property: JsonPropertyName("current_balance")] double CurrentBalance);

    private sealed record NewLedgerFilledPalletDiagnosticResponse(
        [property: JsonPropertyName("production_pallet_id")] long ProductionPalletId,
        [property: JsonPropertyName("prd_doc_id")] long PrdDocId,
        [property: JsonPropertyName("prd_doc_ref")] string PrdDocRef,
        [property: JsonPropertyName("order_id")] long? OrderId,
        [property: JsonPropertyName("order_line_id")] long? OrderLineId,
        [property: JsonPropertyName("item_id")] long ItemId,
        [property: JsonPropertyName("hu_code")] string HuCode,
        [property: JsonPropertyName("planned_qty")] double PlannedQty,
        [property: JsonPropertyName("current_balance")] double CurrentBalance);

    private sealed record NewLedgerDraftPrdLedgerDiagnosticResponse(
        [property: JsonPropertyName("prd_doc_id")] long PrdDocId,
        [property: JsonPropertyName("prd_doc_ref")] string PrdDocRef,
        [property: JsonPropertyName("order_id")] long? OrderId,
        [property: JsonPropertyName("ledger_row_count")] int LedgerRowCount);

    private sealed record NewLedgerTransitionActionResponse(
        [property: JsonPropertyName("action_code")] string ActionCode,
        [property: JsonPropertyName("order_id")] long? OrderId,
        [property: JsonPropertyName("order_ref")] string? OrderRef,
        [property: JsonPropertyName("order_line_id")] long? OrderLineId,
        [property: JsonPropertyName("item_id")] long? ItemId,
        [property: JsonPropertyName("hu_code")] string? HuCode,
        [property: JsonPropertyName("details")] string Details);
}
