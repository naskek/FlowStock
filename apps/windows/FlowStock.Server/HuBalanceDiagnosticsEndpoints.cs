using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class HuBalanceDiagnosticsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/diagnostics/hu-balance/correction-draft", HandleCreateCorrectionDraft);
    }

    private static async Task<IResult> HandleCreateCorrectionDraft(HttpRequest request, IDataStore store)
    {
        var body = await request.ReadFromJsonAsync<CreateHuBalanceCorrectionDraftRequest>();
        if (body == null)
        {
            return Results.BadRequest(new { success = false, error = "INVALID_JSON" });
        }

        var service = new HuBalanceCorrectionService(store);
        var result = service.CreateCorrectionDraft(new HuBalanceCorrectionDraftRequest
        {
            ItemId = body.ItemId,
            LocationId = body.LocationId,
            Comment = body.Comment
        });

        if (!result.Success)
        {
            return Results.BadRequest(MapResult(result, success: false));
        }

        return Results.Ok(MapResult(result, success: true));
    }

    private static object MapResult(HuBalanceCorrectionDraftResult result, bool success)
    {
        return new
        {
            success,
            error = result.Error,
            message = result.Message,
            doc_id = result.DocId,
            doc_ref = result.DocRef,
            line_count = result.LineCount,
            protected_filled_pallets = result.ProtectedFilledPallets.Select(pallet => new
            {
                hu_code = pallet.HuCode,
                prd_doc_id = pallet.PrdDocId,
                prd_ref = pallet.PrdDocRef,
                status = pallet.Status,
                planned_qty = pallet.PlannedQty
            }),
            candidate_balances = result.CandidateBalances.Select(balance => new
            {
                hu_code = balance.HuCode,
                qty = balance.Qty,
                is_protected = balance.Protected
            }),
            total_all = result.TotalAll,
            total_excluding_protected = result.TotalExcludingProtected
        };
    }

    private sealed class CreateHuBalanceCorrectionDraftRequest
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("location_id")]
        public long LocationId { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }
    }
}
