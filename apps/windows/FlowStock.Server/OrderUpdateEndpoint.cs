using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderUpdateEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPut("/api/orders/{orderId:long}", HandleUpdateAsync);
    }

    private static async Task<IResult> HandleUpdateAsync(
        HttpRequest request,
        long orderId,
        IDataStore store,
        PartnerRoleResolver partnerRoles,
        ILogger<OrderUpdateEndpointMarker> logger)
    {
        using var timings = new OperationTimings();
        var rawRequestId = request.Headers["X-FlowStock-Request-Id"].ToString();
        var requestId = Guid.TryParseExact(rawRequestId, "N", out var clientId)
            ? clientId.ToString("N") : request.HttpContext.TraceIdentifier;
        request.HttpContext.Response.OnCompleted(() =>
        {
            logger.LogInformation("order_update_http order_id={OrderId} request_id={RequestId} status={Status} elapsed_ms={ElapsedMs} aborted={Aborted}",
                orderId, requestId, request.HttpContext.Response.StatusCode, timings.ElapsedMs, request.HttpContext.RequestAborted.IsCancellationRequested);
            return Task.CompletedTask;
        });
        try
        {
            return await HandleUpdateCoreAsync(request, orderId, store, partnerRoles, logger);
        }
        finally
        {
            logger.LogInformation("order_update order_id={OrderId} request_id={RequestId} elapsed_ms={ElapsedMs} stages={Stages}",
                orderId, requestId, timings.ElapsedMs, timings.Summary);
        }
    }

    private static async Task<IResult> HandleUpdateCoreAsync(
        HttpRequest request, long orderId, IDataStore store,
        PartnerRoleResolver partnerRoles, ILogger<OrderUpdateEndpointMarker> logger)
    {
        Order? existing;
        using (OperationTimings.Measure("initial_order_read")) existing = store.GetOrder(orderId);
        if (existing == null)
        {
            return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
        }

        if (existing.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            return Results.BadRequest(new ApiResult(false, "ORDER_NOT_EDITABLE"));
        }

        var rawJson = await ReadBodyAsync(request);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Results.BadRequest(new ApiResult(false, "EMPTY_BODY"));
        }

        UpdateOrderRequest? updateRequest;
        try
        {
            updateRequest = JsonSerializer.Deserialize<UpdateOrderRequest>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        if (updateRequest == null)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
        }

        var orderType = OrderStatusMapper.TypeFromString(updateRequest.Type);
        if (!orderType.HasValue)
        {
            return Results.BadRequest(new ApiResult(false, "INVALID_TYPE"));
        }

        if (!string.IsNullOrWhiteSpace(updateRequest.Status))
        {
            var parsedStatus = OrderStatusMapper.StatusFromString(updateRequest.Status);
            if (!parsedStatus.HasValue)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_STATUS"));
            }

            if (parsedStatus.Value == OrderStatus.Shipped)
            {
                return Results.BadRequest(new ApiResult(false, "SHIPPED_STATUS_FORBIDDEN"));
            }

            if (parsedStatus.Value == OrderStatus.Cancelled)
            {
                return Results.BadRequest(new ApiResult(false, "ORDER_CANCEL_USE_STATUS_ENDPOINT"));
            }
        }

        DateTime? dueDate = null;
        if (!string.IsNullOrWhiteSpace(updateRequest.DueDate))
        {
            if (!DateTime.TryParseExact(
                    updateRequest.DueDate.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDueDate))
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_DUE_DATE"));
            }

            dueDate = parsedDueDate.Date;
        }

        long? partnerId = null;
        if (orderType.Value == OrderType.Customer)
        {
            if (!updateRequest.PartnerId.HasValue || updateRequest.PartnerId.Value <= 0)
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_PARTNER_ID"));
            }

            var partner = store.GetPartner(updateRequest.PartnerId.Value);
            if (partner == null)
            {
                return Results.BadRequest(new ApiResult(false, "PARTNER_NOT_FOUND"));
            }

            if (!partnerRoles.IsCustomer(partner.Id))
            {
                return Results.BadRequest(new ApiResult(false, "PARTNER_IS_SUPPLIER"));
            }

            partnerId = partner.Id;
        }

        var inputLines = updateRequest.Lines ?? new List<UpdateOrderLineRequest>();
        if (inputLines.Count == 0)
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_LINES"));
        }

        var existingLineIds = store.GetOrderLines(orderId)
            .Select(line => line.Id)
            .ToHashSet();
        var selectedHuByLineId = new Dictionary<long, IReadOnlyList<string>>();
        var lines = new List<OrderLineView>();
        foreach (var line in inputLines)
        {
            if (line.SelectedHuCodes != null)
            {
                if (!line.OrderLineId.HasValue || line.OrderLineId.Value <= 0 || !existingLineIds.Contains(line.OrderLineId.Value))
                {
                    return Results.BadRequest(new ApiResult(false, "ORDER_LINE_NOT_FOUND"));
                }

                selectedHuByLineId[line.OrderLineId.Value] = line.SelectedHuCodes;
            }

            if (!line.ItemId.HasValue || line.ItemId.Value <= 0)
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_ITEM_ID"));
            }

            if (line.QtyOrdered <= 0)
            {
                if (orderType.Value == OrderType.Customer)
                {
                    return Results.BadRequest(new ApiErrorResult(
                        false,
                        "INVALID_QTY_ORDERED",
                        "Количество строки не может быть 0. Удалите строку заказа."));
                }

                return Results.BadRequest(new ApiResult(false, "INVALID_QTY_ORDERED"));
            }

            var item = store.FindItemById(line.ItemId.Value);
            if (item == null)
            {
                return Results.BadRequest(new ApiResult(false, "ITEM_NOT_FOUND"));
            }

            lines.Add(new OrderLineView
            {
                Id = line.OrderLineId ?? 0,
                OrderId = orderId,
                ItemId = item.Id,
                ItemName = item.Name,
                QtyOrdered = line.QtyOrdered,
                UnitPriceGross = line.UnitPriceGross,
                ChangeUnitPriceGross = line.ChangeUnitPriceGross == true,
                ProductionPurpose = orderType.Value == OrderType.Customer
                    ? ProductionLinePurpose.CustomerOrder
                    : ProductionLinePurposeMapper.FromDbValue(line.ProductionPurpose),
                ProductionPalletGroup = NormalizePalletGroup(line.ProductionPalletGroup)
            });
        }

        var requestedOrderRef = NormalizeOrderRef(updateRequest.OrderRef);
        var authoritativeOrderRef = requestedOrderRef ?? existing.OrderRef;

        if (string.IsNullOrWhiteSpace(authoritativeOrderRef))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_ORDER_REF"));
        }

        var orderService = new OrderService(store);
        try
        {
            orderService.UpdateOrder(
                orderId,
                authoritativeOrderRef,
                partnerId,
                dueDate,
                updateRequest.Comment,
                lines,
                orderType.Value,
                updateRequest.BindReservedStock,
                selectedHuByLineId.Count == 0 ? null : selectedHuByLineId,
                preserveOrderRef: requestedOrderRef == null);
        }
        catch (OrderItemActivityException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
        }
        catch (CommercialTermsException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ApiResult(false, MapKnownArgumentError(ex)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, MapKnownInvalidOperationError(ex), ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Order update failed for order_id={OrderId}", orderId);
            return Results.Json(
                new ApiErrorResult(false, "ORDER_UPDATE_FAILED", ex.Message),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        Order? updated;
        using (OperationTimings.Measure("response_order_read")) updated = store.GetOrder(orderId);
        if (updated == null)
        {
            return Results.Json(new ApiResult(false, "ORDER_UPDATE_FAILED"), statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new UpdateOrderEnvelope
        {
            Ok = true,
            Result = "UPDATED",
            OrderId = updated.Id,
            OrderRef = updated.OrderRef,
            OrderRefChanged = requestedOrderRef != null
                && !string.Equals(requestedOrderRef, updated.OrderRef, StringComparison.Ordinal),
            Type = OrderStatusMapper.TypeToString(updated.Type),
            Status = OrderStatusMapper.StatusToString(updated.Status),
            LineCount = store.GetOrderLines(updated.Id).Count
        });
    }

    private static string MapKnownArgumentError(ArgumentException ex)
    {
        return ex.ParamName switch
        {
            "partnerId" when ex.Message.Contains("обязателен", StringComparison.OrdinalIgnoreCase) => "MISSING_PARTNER_ID",
            "partnerId" when ex.Message.Contains("не найден", StringComparison.OrdinalIgnoreCase) => "PARTNER_NOT_FOUND",
            "status" => "SHIPPED_STATUS_FORBIDDEN",
            "orderRef" => "MISSING_ORDER_REF",
            _ => "ORDER_UPDATE_VALIDATION_FAILED"
        };
    }

    private static string MapKnownInvalidOperationError(InvalidOperationException ex)
    {
        if (ex.Message.Contains("не найден", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_FOUND";
        }

        if (ex.Message.Contains("нельзя редактировать", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_EDITABLE";
        }

        if (ex.Message.Contains("Тип существующего заказа", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_TYPE_MISMATCH";
        }

        if (ex.Message.Contains("Смена типа заказа", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_TYPE_CHANGE_FORBIDDEN";
        }

        if (ex.Message.Contains("Нельзя сменить тип заказа", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_TYPE_CHANGE_FORBIDDEN";
        }

        if (ex.Message.Contains("уже зарезервирован", StringComparison.OrdinalIgnoreCase))
        {
            return "HU_RESERVATION_CONFLICT";
        }

        if (ex.Message.Contains("Нельзя уменьшить количество ниже уже заполненного/отгруженного объема", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Нельзя уменьшить количество ниже уже заполненного/выпущенного объема", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Количество меньше защищенного покрытия", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_LINE_QTY_BELOW_COVERAGE";
        }

        if (ex.Message.Contains("есть заполненные паллеты/HU", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_LINE_HAS_FILLED_PALLETS";
        }

        if (ex.Message.Contains("нельзя удалить строку, защищенное покрытие", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_LINE_HAS_FILLED_PALLETS";
        }

        if (ex.Message.Contains("Автоматически сбрасывать можно только PLANNED-паллеты", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("паллетный план уже напечатан", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("находится в фактическом состоянии", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_LINE_PALLET_PLAN_NOT_PLANNED";
        }

        return "ORDER_UPDATE_FAILED";
    }

    private static string? NormalizeOrderRef(string? orderRef)
    {
        return string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
    }

    private static string? NormalizePalletGroup(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }
}

public sealed class OrderUpdateEndpointMarker
{
}
