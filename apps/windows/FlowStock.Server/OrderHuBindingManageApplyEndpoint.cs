using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderHuBindingManageApplyEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/hu-bindings/manage/apply-final", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, IDataStore store)
    {
        RequestBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<RequestBody>(JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_JSON", Message = "Некорректный JSON." });
        }

        if (body == null)
        {
            return Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_REQUEST", Message = "Пустой запрос." });
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        var serviceRequest = new OrderHuBindingManageApplyRequest
        {
            Mode = body.Mode ?? string.Empty,
            ExpectedHuStates = body.ExpectedHuStates?
                .Select(state => new ManageExpectedHuState
                {
                    HuCode = state.HuCode ?? string.Empty,
                    ItemId = state.ItemId,
                    ExpectedQty = state.ExpectedQty,
                    ExpectedOrderId = state.ExpectedOrderId,
                    ExpectedOrderLineId = state.ExpectedOrderLineId
                })
                .ToArray() ?? Array.Empty<ManageExpectedHuState>(),
            Lines = body.Lines?
                .Select(line => new OrderHuBindingManageApplyLineRequest
                {
                    OrderId = line.OrderId,
                    OrderLineId = line.OrderLineId,
                    ExpectedBoundHuCodes = line.ExpectedBoundHuCodes,
                    FinalHuCodes = line.FinalHuCodes
                })
                .ToArray() ?? Array.Empty<OrderHuBindingManageApplyLineRequest>()
        };

        try
        {
            var result = new OrderHuBindingManageApplyService(store).ApplyFinal(serviceRequest);
            return Results.Ok(MapResponse(result));
        }
        catch (OrderHuBindingApplyFinalException ex)
        {
            return MapError(ex.ErrorCode, ex.Message, ex.Problems);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("зарезервирован", StringComparison.OrdinalIgnoreCase))
        {
            return MapError("HU_RESERVED_BY_OTHER_ORDER", ex.Message, [ex.Message]);
        }
        catch (Exception)
        {
            return Results.Json(
                new ErrorResponse
                {
                    Ok = false,
                    Error = "INTERNAL_SERVER_ERROR",
                    Message = "Не удалось применить изменения привязок HU.",
                    Problems = Array.Empty<string>()
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult MapError(string errorCode, string message, IReadOnlyList<string>? problems)
    {
        var payload = new ErrorResponse
        {
            Ok = false,
            Error = errorCode,
            Message = message,
            Problems = problems ?? Array.Empty<string>()
        };

        return errorCode switch
        {
            "ORDER_NOT_FOUND" or "ORDER_LINE_NOT_FOUND" => Results.NotFound(payload),
            "ORDER_NOT_CUSTOMER" or "ORDER_CLOSED" => Results.Json(payload, statusCode: StatusCodes.Status403Forbidden),
            "INVALID_REQUEST" or "DUPLICATE_HU_IN_REQUEST" => Results.BadRequest(payload),
            _ => Results.Json(payload, statusCode: StatusCodes.Status409Conflict)
        };
    }

    private static ApplyResponse MapResponse(OrderHuBindingManageApplyResult result)
    {
        return new ApplyResponse
        {
            Ok = result.Ok,
            Orders = result.Orders.Select(order => new OrderResponse
            {
                OrderId = order.OrderId,
                AppliedLines = order.AppliedLines.Select(line => new AppliedLineResponse
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
                }).ToArray()
            }).ToArray(),
            Warnings = result.Warnings
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class RequestBody
    {
        [JsonPropertyName("mode")] public string? Mode { get; init; }
        [JsonPropertyName("expected_hu_states")] public IReadOnlyList<ExpectedHuStateBody>? ExpectedHuStates { get; init; }
        [JsonPropertyName("lines")] public IReadOnlyList<LineBody>? Lines { get; init; }
    }

    private sealed class ExpectedHuStateBody
    {
        [JsonPropertyName("hu_code")] public string? HuCode { get; init; }
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("expected_qty")] public double ExpectedQty { get; init; }
        [JsonPropertyName("expected_order_id")] public long? ExpectedOrderId { get; init; }
        [JsonPropertyName("expected_order_line_id")] public long? ExpectedOrderLineId { get; init; }
    }

    private sealed class LineBody
    {
        [JsonPropertyName("order_id")] public long OrderId { get; init; }
        [JsonPropertyName("order_line_id")] public long OrderLineId { get; init; }
        [JsonPropertyName("expected_bound_hu_codes")] public IReadOnlyList<string>? ExpectedBoundHuCodes { get; init; }
        [JsonPropertyName("final_hu_codes")] public IReadOnlyList<string>? FinalHuCodes { get; init; }
    }

    private sealed class ApplyResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("orders")] public IReadOnlyList<OrderResponse> Orders { get; init; } = Array.Empty<OrderResponse>();
        [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    private sealed class OrderResponse
    {
        [JsonPropertyName("order_id")] public long OrderId { get; init; }
        [JsonPropertyName("applied_lines")] public IReadOnlyList<AppliedLineResponse> AppliedLines { get; init; } = Array.Empty<AppliedLineResponse>();
    }

    private sealed class AppliedLineResponse
    {
        [JsonPropertyName("order_line_id")] public long OrderLineId { get; init; }
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("previous_hu_codes")] public IReadOnlyList<string> PreviousHuCodes { get; init; } = Array.Empty<string>();
        [JsonPropertyName("final_hu_codes")] public IReadOnlyList<string> FinalHuCodes { get; init; } = Array.Empty<string>();
        [JsonPropertyName("bound_hu_codes")] public IReadOnlyList<string> BoundHuCodes { get; init; } = Array.Empty<string>();
        [JsonPropertyName("detached_hu_codes")] public IReadOnlyList<string> DetachedHuCodes { get; init; } = Array.Empty<string>();
        [JsonPropertyName("reserved_qty")] public double ReservedQty { get; init; }
        [JsonPropertyName("cancelled_planned_pallet_count")] public int CancelledPlannedPalletCount { get; init; }
        [JsonPropertyName("restored_planned_qty")] public double RestoredPlannedQty { get; init; }
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("error")] public string Error { get; init; } = string.Empty;
        [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
        [JsonPropertyName("problems")] public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    }
}
