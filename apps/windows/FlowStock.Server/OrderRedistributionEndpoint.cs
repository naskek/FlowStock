using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderRedistributionEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/redistribute", HandleRedistributeAsync);
    }

    private static async Task<IResult> HandleRedistributeAsync(HttpRequest request, IDataStore store)
    {
        OrderRedistributeRequest? redistributeRequest;
        try
        {
            redistributeRequest = await request.ReadFromJsonAsync<OrderRedistributeRequest>(JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (redistributeRequest == null)
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        if (redistributeRequest.SourceInternalOrderId <= 0
            || redistributeRequest.TargetCustomerOrderId <= 0
            || redistributeRequest.ItemId <= 0
            || redistributeRequest.Qty <= 0)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_REQUEST"));
        }

        var service = new OrderRedistributionService(store);
        try
        {
            var result = service.Redistribute(
                redistributeRequest.SourceInternalOrderId,
                redistributeRequest.TargetCustomerOrderId,
                redistributeRequest.ItemId,
                redistributeRequest.Qty);

            return Results.Ok(new OrderRedistributeEnvelope
            {
                Ok = true,
                Result = "REDISTRIBUTED",
                SourceOrderId = result.SourceOrderId,
                TargetOrderId = result.TargetOrderId,
                ItemId = result.ItemId,
                QtyTransferred = result.QtyTransferred,
                QtyFromUnproduced = result.QtyFromUnproduced,
                QtyFromProducedStock = result.QtyFromProducedStock,
                SourceQtyOrderedAfter = result.SourceQtyOrderedAfter,
                TargetQtyOrderedAfter = result.TargetQtyOrderedAfter
            });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_QTY"));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, MapKnownInvalidOperationError(ex)));
        }
    }

    private static string MapKnownInvalidOperationError(InvalidOperationException ex)
    {
        var message = ex.Message;
        if (message.Contains("не найден", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_FOUND";
        }

        if (message.Contains("Позиция не найдена", StringComparison.OrdinalIgnoreCase))
        {
            return "SOURCE_LINE_NOT_FOUND";
        }

        if (message.Contains("Недостаточно выпущенного", StringComparison.OrdinalIgnoreCase))
        {
            return "INSUFFICIENT_PRODUCED_STOCK";
        }

        if (message.Contains("резерв складского", StringComparison.OrdinalIgnoreCase)
            || message.Contains("резервирование HU", StringComparison.OrdinalIgnoreCase))
        {
            return "CUSTOMER_RESERVATION_REQUIRED";
        }

        if (message.Contains("Нет доступного объема", StringComparison.OrdinalIgnoreCase))
        {
            return "NOTHING_TO_TRANSFER";
        }

        return "REDISTRIBUTION_FAILED";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
