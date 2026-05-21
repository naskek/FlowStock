using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderProducedHuReservationEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{customerOrderId:long}/reserve-produced-hu", HandleReserveAsync);
    }

    private static async Task<IResult> HandleReserveAsync(
        long customerOrderId,
        HttpRequest request,
        IDataStore store)
    {
        OrderReserveProducedHuRequest? reserveRequest;
        try
        {
            reserveRequest = await request.ReadFromJsonAsync<OrderReserveProducedHuRequest>(JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (reserveRequest == null)
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        if (reserveRequest.SourceInternalOrderId <= 0
            || reserveRequest.ItemId <= 0
            || (reserveRequest.HuCodes == null || reserveRequest.HuCodes.Count == 0)
               && reserveRequest.Qty <= 0)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_REQUEST"));
        }

        var service = new OrderProducedHuReservationService(store);
        try
        {
            var result = service.Reserve(new OrderProducedHuReservationRequest
            {
                SourceInternalOrderId = reserveRequest.SourceInternalOrderId,
                TargetCustomerOrderId = customerOrderId,
                ItemId = reserveRequest.ItemId,
                TargetOrderLineId = reserveRequest.TargetOrderLineId,
                HuCodes = reserveRequest.HuCodes,
                Qty = reserveRequest.Qty > 0 ? reserveRequest.Qty : null
            });

            return Results.Ok(new OrderReserveProducedHuEnvelope
            {
                Ok = true,
                Result = "RESERVED",
                SourceInternalOrderId = result.SourceInternalOrderId,
                TargetCustomerOrderId = result.TargetCustomerOrderId,
                ItemId = result.ItemId,
                TargetOrderLineId = result.TargetOrderLineId,
                QtyReserved = result.QtyReserved,
                SourceQtyOrdered = result.SourceQtyOrdered,
                SourceProducedQty = result.SourceProducedQty,
                ReservedHuCodes = result.ReservedHuCodes
            });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_QTY"));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, MapKnownInvalidOperationError(ex), ex.Message));
        }
    }

    private static string MapKnownInvalidOperationError(InvalidOperationException ex)
    {
        var message = ex.Message;
        if (message.Contains("не найден", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_FOUND";
        }

        if (message.Contains("должен быть внутренним", StringComparison.OrdinalIgnoreCase))
        {
            return "SOURCE_NOT_INTERNAL";
        }

        if (message.Contains("должен быть клиентским", StringComparison.OrdinalIgnoreCase))
        {
            return "TARGET_NOT_CUSTOMER";
        }

        if (message.Contains("недоступен для резервирования", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_EDITABLE";
        }

        if (message.Contains("резерв складского", StringComparison.OrdinalIgnoreCase)
            || message.Contains("резервирование HU", StringComparison.OrdinalIgnoreCase))
        {
            return "CUSTOMER_RESERVATION_REQUIRED";
        }

        if (message.Contains("Недостаточно готовых HU", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не имеет положительного остатка", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не является готовой", StringComparison.OrdinalIgnoreCase))
        {
            return "INSUFFICIENT_READY_HU";
        }

        if (message.Contains("уже зарезервирован", StringComparison.OrdinalIgnoreCase))
        {
            return "HU_ALREADY_RESERVED";
        }

        return "RESERVE_PRODUCED_HU_FAILED";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
