using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderHuBindingApplyFinalEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{customerOrderId:long}/hu-bindings/apply-final", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        long customerOrderId,
        HttpRequest request,
        IDataStore store)
    {
        OrderHuBindingApplyFinalRequestBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<OrderHuBindingApplyFinalRequestBody>(JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (body == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_REQUEST"));
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        var service = new OrderHuBindingApplyFinalService(store);
        try
        {
            var result = service.ApplyFinal(
                customerOrderId,
                new OrderHuBindingApplyFinalRequest
                {
                    Mode = body.Mode ?? string.Empty,
                    Lines = body.Lines?
                        .Select(line => new OrderHuBindingApplyFinalLineRequest
                        {
                            OrderLineId = line.OrderLineId,
                            ExpectedBoundHuCodes = line.ExpectedBoundHuCodes,
                            FinalHuCodes = line.FinalHuCodes
                        })
                        .ToArray() ?? Array.Empty<OrderHuBindingApplyFinalLineRequest>()
                });

            return Results.Ok(MapResponse(result));
        }
        catch (OrderHuBindingApplyFinalException ex)
        {
            return Results.BadRequest(new OrderHuBindingApplyFinalErrorResponse
            {
                Ok = false,
                Error = ex.ErrorCode,
                Message = ex.Message,
                Problems = ex.Problems
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("зарезервирован", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new OrderHuBindingApplyFinalErrorResponse
            {
                Ok = false,
                Error = "HU_RESERVED_BY_OTHER_ORDER",
                Message = ex.Message,
                Problems = [ex.Message]
            });
        }
    }

    private static OrderHuBindingApplyFinalResponse MapResponse(OrderHuBindingApplyFinalResult result)
    {
        return new OrderHuBindingApplyFinalResponse
        {
            Ok = result.Ok,
            OrderId = result.OrderId,
            AppliedLines = result.AppliedLines.Select(line => new OrderHuBindingApplyFinalLineResponse
            {
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                PreviousHuCodes = line.PreviousHuCodes,
                FinalHuCodes = line.FinalHuCodes,
                BoundHuCodes = line.BoundHuCodes,
                DetachedHuCodes = line.DetachedHuCodes,
                ReservedQty = line.ReservedQty,
                CancelledPlannedPalletCount = line.CancelledPlannedPalletCount,
                RestoredPlannedQty = line.RestoredPlannedQty
            }).ToArray(),
            Warnings = result.Warnings
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class OrderHuBindingApplyFinalRequestBody
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("lines")]
        public IReadOnlyList<OrderHuBindingApplyFinalLineRequestBody>? Lines { get; init; }
    }

    private sealed class OrderHuBindingApplyFinalLineRequestBody
    {
        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("expected_bound_hu_codes")]
        public IReadOnlyList<string>? ExpectedBoundHuCodes { get; init; }

        [JsonPropertyName("final_hu_codes")]
        public IReadOnlyList<string>? FinalHuCodes { get; init; }
    }

    private sealed class OrderHuBindingApplyFinalResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("applied_lines")]
        public IReadOnlyList<OrderHuBindingApplyFinalLineResponse> AppliedLines { get; init; } =
            Array.Empty<OrderHuBindingApplyFinalLineResponse>();

        [JsonPropertyName("warnings")]
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    private sealed class OrderHuBindingApplyFinalLineResponse
    {
        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("previous_hu_codes")]
        public IReadOnlyList<string> PreviousHuCodes { get; init; } = Array.Empty<string>();

        [JsonPropertyName("final_hu_codes")]
        public IReadOnlyList<string> FinalHuCodes { get; init; } = Array.Empty<string>();

        [JsonPropertyName("bound_hu_codes")]
        public IReadOnlyList<string> BoundHuCodes { get; init; } = Array.Empty<string>();

        [JsonPropertyName("detached_hu_codes")]
        public IReadOnlyList<string> DetachedHuCodes { get; init; } = Array.Empty<string>();

        [JsonPropertyName("reserved_qty")]
        public double ReservedQty { get; init; }

        [JsonPropertyName("cancelled_planned_pallet_count")]
        public int CancelledPlannedPalletCount { get; init; }

        [JsonPropertyName("restored_planned_qty")]
        public double RestoredPlannedQty { get; init; }
    }

    private sealed class OrderHuBindingApplyFinalErrorResponse
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
