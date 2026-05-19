using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class NegativeStockDiagnosticsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/diagnostics/negative-stock", HandleListNegativeStock);
        app.MapPost("/api/diagnostics/negative-stock/correction-draft", HandleCreateCorrectionDraft);
    }

    private static IResult HandleListNegativeStock(IDataStore store)
    {
        var rows = store.GetNegativeStockBalances()
            .Select(MapNegativeStockRow)
            .ToArray();
        return Results.Ok(new { success = true, rows });
    }

    private static async Task<IResult> HandleCreateCorrectionDraft(HttpRequest request, IDataStore store)
    {
        var body = await request.ReadFromJsonAsync<CreateCorrectionDraftRequest>();
        if (body == null)
        {
            return Results.BadRequest(new { success = false, error = "INVALID_JSON" });
        }

        var service = new NegativeStockCorrectionService(store);
        var result = service.CreateCorrectionDraft(new NegativeStockCorrectionDraftRequest
        {
            ItemId = body.ItemId,
            LocationId = body.LocationId,
            HuCode = body.HuCode,
            QtyToCompensate = body.QtyToCompensate,
            SourceDocId = body.SourceDocId,
            SourceLedgerEntryId = body.SourceLedgerEntryId,
            Comment = body.Comment
        });

        if (!result.Success)
        {
            return Results.BadRequest(new
            {
                success = false,
                error = result.Error,
                message = result.Message
            });
        }

        return Results.Ok(new
        {
            success = true,
            doc_id = result.DocId,
            doc_ref = result.DocRef,
            message = result.Message
        });
    }

    private static object MapNegativeStockRow(NegativeStockBalanceRow row) => new
    {
        item_id = row.ItemId,
        item_name = row.ItemName,
        location_id = row.LocationId,
        location_code = row.LocationCode,
        hu_code = row.HuCode,
        qty = row.Qty,
        last_ledger_entry_id = row.LastLedgerEntryId,
        last_doc_id = row.LastDocId,
        last_doc_ref = row.LastDocRef,
        last_doc_type = row.LastDocType.HasValue ? DocTypeMapper.ToOpString(row.LastDocType.Value) : null,
        order_id = row.OrderId,
        order_ref = row.OrderRef,
        last_movement_at = row.LastMovementAt?.ToString("O")
    };

    private sealed class CreateCorrectionDraftRequest
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("location_id")]
        public long LocationId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("qty_to_compensate")]
        public double QtyToCompensate { get; init; }

        [JsonPropertyName("source_doc_id")]
        public long? SourceDocId { get; init; }

        [JsonPropertyName("source_ledger_entry_id")]
        public long? SourceLedgerEntryId { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }
    }
}
