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
            line_count = result.LineCount,
            message = result.Message
        });
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
