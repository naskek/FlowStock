using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderHuReservationApplyEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{customerOrderId:long}/hu-reservations/apply", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        long customerOrderId,
        HttpRequest request,
        IDataStore store)
    {
        OrderHuReservationApplyRequestBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<OrderHuReservationApplyRequestBody>(JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (body?.Lines == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_REQUEST"));
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        var service = new OrderHuReservationApplyService(store);
        try
        {
            var result = service.Apply(
                customerOrderId,
                new OrderHuReservationApplyRequest
                {
                    Lines = body.Lines
                        .Select(line => new OrderHuReservationApplyLineRequest
                        {
                            OrderLineId = line.OrderLineId,
                            SelectedHuCodes = line.SelectedHuCodes ?? Array.Empty<string>()
                        })
                        .ToArray()
                });

            return Results.Ok(MapResponse(result));
        }
        catch (OrderHuReservationApplyException ex)
        {
            return Results.BadRequest(new OrderHuReservationApplyErrorResponse
            {
                Ok = false,
                Error = ex.ErrorCode,
                Message = ex.Message,
                Problems = ex.Problems
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("зарезервирован", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new OrderHuReservationApplyErrorResponse
            {
                Ok = false,
                Error = "HU_RESERVED_BY_OTHER_ORDER",
                Message = ex.Message,
                Problems = [ex.Message]
            });
        }
    }

    private static OrderHuReservationApplyResponse MapResponse(OrderHuReservationApplyResult result)
    {
        return new OrderHuReservationApplyResponse
        {
            Ok = result.Ok,
            OrderId = result.OrderId,
            AppliedLines = result.AppliedLines
                .Select(line => new OrderHuReservationApplyLineResponse
                {
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    OrderedQty = line.OrderedQty,
                    ReservedQty = line.ReservedQty,
                    SelectedHuCount = line.SelectedHuCount,
                    SelectedHu = line.SelectedHu
                        .Select(hu => new OrderHuReservationAppliedHuResponse
                        {
                            HuCode = hu.HuCode,
                            Source = hu.Source,
                            Qty = hu.Qty,
                            ShipReady = hu.ShipReady
                        })
                        .ToArray()
                })
                .ToArray(),
            Warnings = result.Warnings
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class OrderHuReservationApplyRequestBody
    {
        [JsonPropertyName("lines")]
        public IReadOnlyList<OrderHuReservationApplyLineRequestBody>? Lines { get; init; }
    }

    private sealed class OrderHuReservationApplyLineRequestBody
    {
        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long? ItemId { get; init; }

        [JsonPropertyName("selected_hu_codes")]
        public IReadOnlyList<string>? SelectedHuCodes { get; init; }
    }

    private sealed class OrderHuReservationApplyResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("applied_lines")]
        public IReadOnlyList<OrderHuReservationApplyLineResponse> AppliedLines { get; init; } =
            Array.Empty<OrderHuReservationApplyLineResponse>();

        [JsonPropertyName("warnings")]
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    private sealed class OrderHuReservationApplyLineResponse
    {
        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("ordered_qty")]
        public double OrderedQty { get; init; }

        [JsonPropertyName("reserved_qty")]
        public double ReservedQty { get; init; }

        [JsonPropertyName("selected_hu_count")]
        public int SelectedHuCount { get; init; }

        [JsonPropertyName("selected_hu")]
        public IReadOnlyList<OrderHuReservationAppliedHuResponse> SelectedHu { get; init; } =
            Array.Empty<OrderHuReservationAppliedHuResponse>();
    }

    private sealed class OrderHuReservationAppliedHuResponse
    {
        [JsonPropertyName("hu_code")]
        public string HuCode { get; init; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("ship_ready")]
        public bool ShipReady { get; init; }
    }

    private sealed class OrderHuReservationApplyErrorResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("error")]
        public string Error { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("problems")]
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    }
}
