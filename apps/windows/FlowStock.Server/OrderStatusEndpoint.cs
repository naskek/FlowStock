using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderStatusEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{orderId:long}/status", HandleStatusAsync);
    }

    private static async Task<IResult> HandleStatusAsync(HttpRequest request, long orderId, IDataStore store)
    {
        var existing = store.GetOrder(orderId);
        if (existing == null)
        {
            return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
        }

        var rawJson = await ReadBodyAsync(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        SetOrderStatusRequest? statusRequest;
        try
        {
            statusRequest = JsonSerializer.Deserialize<SetOrderStatusRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        var parsedStatus = OrderStatusMapper.StatusFromString(statusRequest?.Status);
        if (!parsedStatus.HasValue)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_STATUS"));
        }

        if (parsedStatus.Value != OrderStatus.Cancelled)
        {
            return Results.BadRequest(new ApiResult(false, "ORDER_STATUS_MANUAL_DISABLED"));
        }

        var orderService = new OrderService(store);
        try
        {
            orderService.ChangeOrderStatus(orderId, parsedStatus.Value);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, MapKnownInvalidOperationError(ex)));
        }

        var updated = store.GetOrder(orderId);
        return Results.Ok(new SetOrderStatusEnvelope
        {
            Ok = true,
            Result = "STATUS_CHANGED",
            OrderId = orderId,
            Status = OrderStatusMapper.StatusToString(updated?.Status ?? OrderStatus.Cancelled)
        });
    }

    private static string MapKnownInvalidOperationError(InvalidOperationException ex)
    {
        if (ex.Message.Contains("Заказ не найден", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_FOUND";
        }

        if (ex.Message.Contains("нельзя отменить", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_CANCEL_FORBIDDEN";
        }

        if (ex.Message.Contains("Ручное изменение статуса", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_STATUS_MANUAL_DISABLED";
        }

        return "ORDER_STATUS_CHANGE_FAILED";
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }
}
