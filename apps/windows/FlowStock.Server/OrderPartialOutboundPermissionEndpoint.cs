using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderPartialOutboundPermissionEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPut("/api/orders/{orderId:long}/partial-outbound-permission", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        long orderId,
        HttpRequest request,
        IDataStore store,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FlowStock.Server.OrderOperations");
        var rawJson = await ReadBodyAsync(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Failure(StatusCodes.Status400BadRequest, "EMPTY_BODY", orderId, logger);
        }

        SetOrderPartialOutboundPermissionRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<SetOrderPartialOutboundPermissionRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Failure(StatusCodes.Status400BadRequest, "INVALID_JSON", orderId, logger);
        }

        if (body?.AllowPartialOutbound == null)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                "MISSING_ALLOW_PARTIAL_OUTBOUND",
                orderId,
                logger,
                deviceId: body?.DeviceId);
        }

        var requestedValue = body.AllowPartialOutbound.Value;
        var deviceId = string.IsNullOrWhiteSpace(body.DeviceId) ? null : body.DeviceId.Trim();
        SetOrderPartialOutboundPermissionResponse? response = null;
        bool? oldValue = null;

        ServerOperationLogging.TryLogOrderPartialOutboundPermissionOperation(
            logger,
            LogLevel.Information,
            phase: "ATTEMPT",
            result: "PENDING",
            orderId: orderId,
            requestedValue: requestedValue,
            deviceId: deviceId);

        try
        {
            store.ExecuteInTransaction(transactionStore =>
            {
                if (!transactionStore.LockOrdersForUpdate([orderId]))
                {
                    response = Error(orderId, "ORDER_NOT_FOUND", "Заказ не найден.");
                    return;
                }

                var order = transactionStore.GetOrder(orderId);
                if (order == null)
                {
                    response = Error(orderId, "ORDER_NOT_FOUND", "Заказ не найден.");
                    return;
                }

                oldValue = order.EffectiveAllowPartialOutbound;

                if (order.Type != OrderType.Customer)
                {
                    response = Error(order, "ORDER_PARTIAL_OUTBOUND_NOT_CUSTOMER", "Разрешение доступно только для клиентского заказа.");
                    return;
                }

                if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
                {
                    response = Error(order, "ORDER_PARTIAL_OUTBOUND_TERMINAL", "Терминальный заказ нельзя изменять.");
                    return;
                }

                if (order.Status is not (OrderStatus.InProgress or OrderStatus.Accepted))
                {
                    response = Error(order, "ORDER_PARTIAL_OUTBOUND_NOT_ACTIVE", "Разрешение доступно только для активного сохранённого заказа.");
                    return;
                }

                var changed = oldValue.Value != requestedValue;
                if (changed)
                {
                    transactionStore.UpdateOrderPartialOutboundPermission(orderId, requestedValue);
                }

                response = new SetOrderPartialOutboundPermissionResponse
                {
                    Ok = true,
                    Result = changed ? "UPDATED" : "UNCHANGED",
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    Status = OrderStatusMapper.StatusToString(order.Status),
                    AllowPartialOutbound = requestedValue,
                    Changed = changed
                };

            });
        }
        catch (Exception ex)
        {
            try
            {
                logger.LogError(ex, "Partial outbound permission update failed for order_id={OrderId}", orderId);
            }
            catch
            {
                // Diagnostic logging must never affect the command result.
            }
            ServerOperationLogging.TryLogOrderPartialOutboundPermissionOperation(
                logger,
                LogLevel.Error,
                phase: "RESULT",
                result: "FAILURE",
                orderId: orderId,
                oldValue: oldValue,
                requestedValue: requestedValue,
                errorCode: "ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE_FAILED",
                deviceId: deviceId);
            return Results.Json(
                Error(orderId, "ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE_FAILED", "Не удалось изменить разрешение частичной отгрузки."),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        response ??= Error(orderId, "ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE_FAILED", "Не удалось изменить разрешение частичной отгрузки.");
        if (!response.Ok)
        {
            ServerOperationLogging.TryLogOrderPartialOutboundPermissionOperation(
                logger,
                LogLevel.Warning,
                phase: "RESULT",
                result: "FAILURE",
                orderId: orderId,
                orderRef: response.OrderRef,
                oldValue: oldValue,
                requestedValue: requestedValue,
                resultingValue: response.AllowPartialOutbound,
                changed: false,
                errorCode: response.Error,
                deviceId: deviceId);
            return response.Error == "ORDER_NOT_FOUND"
                ? Results.NotFound(response)
                : Results.BadRequest(response);
        }

        ServerOperationLogging.TryLogOrderPartialOutboundPermissionOperation(
            logger,
            LogLevel.Information,
            phase: "RESULT",
            result: "SUCCESS",
            orderId: response.OrderId,
            orderRef: response.OrderRef,
            oldValue: oldValue,
            requestedValue: requestedValue,
            resultingValue: response.AllowPartialOutbound,
            changed: response.Changed,
            deviceId: deviceId);

        return Results.Ok(response);
    }

    private static IResult Failure(
        int statusCode,
        string errorCode,
        long orderId,
        ILogger logger,
        string? deviceId = null)
    {
        ServerOperationLogging.TryLogOrderPartialOutboundPermissionOperation(
            logger,
            LogLevel.Information,
            phase: "ATTEMPT",
            result: "PENDING",
            orderId: orderId,
            deviceId: deviceId);
        ServerOperationLogging.TryLogOrderPartialOutboundPermissionOperation(
            logger,
            LogLevel.Warning,
            phase: "RESULT",
            result: "FAILURE",
            orderId: orderId,
            errorCode: errorCode,
            deviceId: deviceId);
        return Results.Json(Error(orderId, errorCode, null), statusCode: statusCode);
    }

    private static SetOrderPartialOutboundPermissionResponse Error(long orderId, string error, string? message) => new()
    {
        Ok = false,
        Error = error,
        Message = message,
        OrderId = orderId
    };

    private static SetOrderPartialOutboundPermissionResponse Error(Order order, string error, string message) => new()
    {
        Ok = false,
        Error = error,
        Message = message,
        OrderId = order.Id,
        OrderRef = order.OrderRef,
        Status = OrderStatusMapper.StatusToString(order.Status),
        AllowPartialOutbound = order.EffectiveAllowPartialOutbound
    };

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }
}
