using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderHuReservationCandidatesEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/hu-reservation-candidates", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, IDataStore store)
    {
        HuReservationCandidatesRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<HuReservationCandidatesRequest>(JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (body == null)
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        if (body.Lines == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_REQUEST"));
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        var service = new HuReservationCandidatesService(store);
        var result = service.Build(new HuReservationCandidatesQuery
        {
            OrderId = body.OrderId,
            Lines = body.Lines
                .Select(line => new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = line.ClientLineKey ?? string.Empty,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                })
                .ToArray(),
            ExcludeHuCodes = body.ExcludeHuCodes ?? Array.Empty<string>()
        });

        return Results.Ok(MapResponse(result));
    }

    private static HuReservationCandidatesResponse MapResponse(HuReservationCandidatesResult result)
    {
        return new HuReservationCandidatesResponse
        {
            Lines = result.Lines
                .Select(line => new HuReservationCandidatesLineResponse
                {
                    ClientLineKey = line.ClientLineKey,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered,
                    AvailableQty = line.AvailableQty,
                    AutoSelectedQty = line.AutoSelectedQty,
                    Candidates = line.Candidates
                        .Select(candidate => new HuReservationCandidateResponse
                        {
                            HuCode = candidate.HuCode,
                            Source = candidate.Source,
                            SourceOrderId = candidate.SourceOrderId,
                            SourceOrderRef = candidate.SourceOrderRef,
                            SourcePrdDocId = candidate.SourcePrdDocId,
                            SourcePrdRef = candidate.SourcePrdRef,
                            Qty = candidate.Qty,
                            ShipReady = candidate.ShipReady,
                            AutoSelected = candidate.AutoSelected,
                            ReservedByOrderId = candidate.ReservedByOrderId,
                            ReservedByOrderRef = candidate.ReservedByOrderRef,
                            Note = candidate.Note
                        })
                        .ToArray()
                })
                .ToArray()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class HuReservationCandidatesRequest
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("lines")]
        public IReadOnlyList<HuReservationCandidatesLineRequest>? Lines { get; init; }

        [JsonPropertyName("exclude_hu_codes")]
        public IReadOnlyList<string>? ExcludeHuCodes { get; init; }
    }

    private sealed class HuReservationCandidatesLineRequest
    {
        [JsonPropertyName("client_line_key")]
        public string? ClientLineKey { get; init; }

        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }
    }

    private sealed class HuReservationCandidatesResponse
    {
        [JsonPropertyName("lines")]
        public IReadOnlyList<HuReservationCandidatesLineResponse> Lines { get; init; } = Array.Empty<HuReservationCandidatesLineResponse>();
    }

    private sealed class HuReservationCandidatesLineResponse
    {
        [JsonPropertyName("client_line_key")]
        public string ClientLineKey { get; init; } = string.Empty;

        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }

        [JsonPropertyName("available_qty")]
        public double AvailableQty { get; init; }

        [JsonPropertyName("auto_selected_qty")]
        public double AutoSelectedQty { get; init; }

        [JsonPropertyName("candidates")]
        public IReadOnlyList<HuReservationCandidateResponse> Candidates { get; init; } = Array.Empty<HuReservationCandidateResponse>();
    }

    private sealed class HuReservationCandidateResponse
    {
        [JsonPropertyName("hu_code")]
        public string HuCode { get; init; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("source_order_id")]
        public long? SourceOrderId { get; init; }

        [JsonPropertyName("source_order_ref")]
        public string? SourceOrderRef { get; init; }

        [JsonPropertyName("source_prd_doc_id")]
        public long? SourcePrdDocId { get; init; }

        [JsonPropertyName("source_prd_ref")]
        public string? SourcePrdRef { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("ship_ready")]
        public bool ShipReady { get; init; }

        [JsonPropertyName("auto_selected")]
        public bool AutoSelected { get; init; }

        [JsonPropertyName("reserved_by_order_id")]
        public long? ReservedByOrderId { get; init; }

        [JsonPropertyName("reserved_by_order_ref")]
        public string? ReservedByOrderRef { get; init; }

        [JsonPropertyName("note")]
        public string Note { get; init; } = string.Empty;
    }
}
