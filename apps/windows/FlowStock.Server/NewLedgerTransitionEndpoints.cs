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
        app.MapPost("/api/admin/maintenance/new-ledger-transition/filled-ledger-repair/dry-run", HandleFilledLedgerRepairDryRun);
        app.MapPost("/api/admin/maintenance/new-ledger-transition/filled-ledger-repair/apply", HandleFilledLedgerRepairApply);
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

    private static IResult HandleFilledLedgerRepairDryRun(FilledLedgerRepairEndpointRequest? request, IDataStore store)
    {
        try
        {
            var report = new NewLedgerTransitionService(store).DryRunFilledLedgerRepair(MapRepairRequest(request));
            return Results.Ok(MapFilledLedgerRepairReport(report));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new ApiResult(false, ex.Message));
        }
    }

    private static async Task<IResult> HandleFilledLedgerRepairApply(FilledLedgerRepairEndpointRequest request, IDataStore store)
    {
        if (!string.Equals(request.Confirm, "APPLY", StringComparison.Ordinal))
        {
            return Results.BadRequest(new ApiResult(false, "CONFIRM_APPLY_REQUIRED"));
        }

        await ApplyLock.WaitAsync();
        try
        {
            var report = new NewLedgerTransitionService(store).ApplyFilledLedgerRepair(MapRepairRequest(request));
            return Results.Ok(MapFilledLedgerRepairReport(report));
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

    private static FilledLedgerRepairRequest MapRepairRequest(FilledLedgerRepairEndpointRequest? request) =>
        new()
        {
            OrderIds = request?.OrderIds ?? Array.Empty<long>(),
            PrdDocIds = request?.PrdDocIds ?? Array.Empty<long>(),
            PalletIds = request?.PalletIds ?? Array.Empty<long>(),
            CloseStaleInternalPrdDrafts = request?.CloseStaleInternalPrdDrafts ?? false
        };

    private static FilledLedgerRepairResponse MapFilledLedgerRepairReport(FilledLedgerRepairReport report) =>
        new(
            true,
            report.DryRun,
            report.LedgerRowsWritten,
            report.ClosedPrdDocIds,
            report.RefreshedOrderIds,
            report.AppliedPalletIds,
            report.Candidates.Select(MapFilledLedgerRepairCandidate).ToArray(),
            report.StaleInternalPrdDraftCloseCandidates.Select(MapPrdCloseCandidate).ToArray(),
            report.Skipped.Select(MapFilledLedgerRepairCandidate).ToArray(),
            report.Warnings);

    private static FilledLedgerRepairCandidateResponse MapFilledLedgerRepairCandidate(FilledLedgerRepairCandidate row) =>
        new(
            row.OrderId,
            row.OrderRef,
            row.OrderType,
            row.OrderStatus,
            row.PrdDocId,
            row.PrdDocRef,
            row.PrdStatus,
            row.PalletId,
            row.HuCode,
            row.ItemId,
            row.LocationId,
            row.PlannedQty,
            row.CurrentReceiptQty,
            row.CurrentBalanceQty,
            row.Decision);

    private static FilledLedgerRepairPrdCloseCandidateResponse MapPrdCloseCandidate(FilledLedgerRepairPrdCloseCandidate row) =>
        new(row.DocId, row.DocRef, row.OrderId, row.OrderRef, row.GrossReceiptQtyByOrder, row.OrderedQtyByOrder, row.Reason);

    private sealed class NewLedgerTransitionApplyRequest
    {
        [JsonPropertyName("confirm")]
        public string? Confirm { get; init; }
    }

    private sealed class FilledLedgerRepairEndpointRequest
    {
        [JsonPropertyName("order_ids")]
        public long[]? OrderIds { get; init; }

        [JsonPropertyName("prd_doc_ids")]
        public long[]? PrdDocIds { get; init; }

        [JsonPropertyName("pallet_ids")]
        public long[]? PalletIds { get; init; }

        [JsonPropertyName("close_stale_internal_prd_drafts")]
        public bool CloseStaleInternalPrdDrafts { get; init; }

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

    private sealed record FilledLedgerRepairResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("dry_run")] bool DryRun,
        [property: JsonPropertyName("ledger_rows_written")] int LedgerRowsWritten,
        [property: JsonPropertyName("closed_prd_doc_ids")] IReadOnlyList<long> ClosedPrdDocIds,
        [property: JsonPropertyName("refreshed_order_ids")] IReadOnlyList<long> RefreshedOrderIds,
        [property: JsonPropertyName("applied_pallet_ids")] IReadOnlyList<long> AppliedPalletIds,
        [property: JsonPropertyName("candidates")] IReadOnlyList<FilledLedgerRepairCandidateResponse> Candidates,
        [property: JsonPropertyName("stale_internal_prd_draft_close_candidates")] IReadOnlyList<FilledLedgerRepairPrdCloseCandidateResponse> StaleInternalPrdDraftCloseCandidates,
        [property: JsonPropertyName("skipped")] IReadOnlyList<FilledLedgerRepairCandidateResponse> Skipped,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

    private sealed record FilledLedgerRepairCandidateResponse(
        [property: JsonPropertyName("order_id")] long? OrderId,
        [property: JsonPropertyName("order_ref")] string OrderRef,
        [property: JsonPropertyName("order_type")] string OrderType,
        [property: JsonPropertyName("order_status")] string OrderStatus,
        [property: JsonPropertyName("prd_doc_id")] long PrdDocId,
        [property: JsonPropertyName("prd_doc_ref")] string PrdDocRef,
        [property: JsonPropertyName("prd_status")] string PrdStatus,
        [property: JsonPropertyName("pallet_id")] long PalletId,
        [property: JsonPropertyName("hu_code")] string HuCode,
        [property: JsonPropertyName("item_id")] long ItemId,
        [property: JsonPropertyName("location_id")] long? LocationId,
        [property: JsonPropertyName("planned_qty")] double PlannedQty,
        [property: JsonPropertyName("current_receipt_qty")] double CurrentReceiptQty,
        [property: JsonPropertyName("current_balance_qty")] double CurrentBalanceQty,
        [property: JsonPropertyName("decision")] string Decision);

    private sealed record FilledLedgerRepairPrdCloseCandidateResponse(
        [property: JsonPropertyName("doc_id")] long DocId,
        [property: JsonPropertyName("doc_ref")] string DocRef,
        [property: JsonPropertyName("order_id")] long OrderId,
        [property: JsonPropertyName("order_ref")] string OrderRef,
        [property: JsonPropertyName("gross_receipt_qty_by_order")] double GrossReceiptQtyByOrder,
        [property: JsonPropertyName("ordered_qty_by_order")] double OrderedQtyByOrder,
        [property: JsonPropertyName("reason")] string Reason);
}
