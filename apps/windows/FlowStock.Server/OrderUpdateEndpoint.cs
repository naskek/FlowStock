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

    private static async Task<IResult> HandleUpdateAsync(HttpRequest request, long orderId, IDataStore store)
    {
        var existing = store.GetOrder(orderId);
        if (existing == null)
        {
            return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
        }

        if (existing.Status == OrderStatus.Shipped)
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

            var partnerStatuses = LoadPartnerStatuses();
            var partnerRole = partnerStatuses.TryGetValue(partner.Id, out var storedRole)
                ? storedRole
                : LocalPartnerRole.Both;
            if (partnerRole == LocalPartnerRole.Supplier)
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

        var lines = new List<OrderLineView>();
        foreach (var line in inputLines)
        {
            if (!line.ItemId.HasValue || line.ItemId.Value <= 0)
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_ITEM_ID"));
            }

            if (line.QtyOrdered <= 0)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_QTY_ORDERED"));
            }

            var item = store.FindItemById(line.ItemId.Value);
            if (item == null)
            {
                return Results.BadRequest(new ApiResult(false, "ITEM_NOT_FOUND"));
            }

            lines.Add(new OrderLineView
            {
                ItemId = item.Id,
                ItemName = item.Name,
                QtyOrdered = line.QtyOrdered
            });
        }

        var requestedOrderRef = NormalizeOrderRef(updateRequest.OrderRef);
        var authoritativeOrderRef = requestedOrderRef ?? existing.OrderRef;
        var orderRefChanged = false;

        if (string.IsNullOrWhiteSpace(authoritativeOrderRef))
        {
            return Results.BadRequest(new ApiResult(false, "MISSING_ORDER_REF"));
        }

        if (!string.IsNullOrWhiteSpace(requestedOrderRef)
            && store.GetOrders().Any(order =>
                order.Id != orderId
                && string.Equals(order.OrderRef, authoritativeOrderRef, StringComparison.OrdinalIgnoreCase)))
        {
            authoritativeOrderRef = GenerateNextOrderRef(store);
            orderRefChanged = true;
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
                orderType.Value);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ApiResult(false, MapKnownArgumentError(ex)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, MapKnownInvalidOperationError(ex)));
        }

        var updated = store.GetOrder(orderId);
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
            OrderRefChanged = orderRefChanged,
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

        return "ORDER_UPDATE_FAILED";
    }

    private static string? NormalizeOrderRef(string? orderRef)
    {
        return string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim();
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }

    private static string GenerateNextOrderRef(IDataStore store)
    {
        long max = 0;
        foreach (var order in store.GetOrders())
        {
            var orderRef = order.OrderRef?.Trim();
            if (string.IsNullOrWhiteSpace(orderRef) || !IsDigitsOnly(orderRef))
            {
                continue;
            }

            if (long.TryParse(orderRef, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value > max)
            {
                max = value;
            }
        }

        return (max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    private static bool IsDigitsOnly(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static IReadOnlyDictionary<long, LocalPartnerRole> LoadPartnerStatuses()
    {
        var path = Path.Combine(ServerPaths.BaseDir, "partner_statuses.json");
        if (!File.Exists(path))
        {
            return new Dictionary<long, LocalPartnerRole>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var data = JsonSerializer.Deserialize<Dictionary<long, LocalPartnerRole>>(json, options);
            return data ?? new Dictionary<long, LocalPartnerRole>();
        }
        catch
        {
            return new Dictionary<long, LocalPartnerRole>();
        }
    }

    private enum LocalPartnerRole
    {
        Client,
        Supplier,
        Both
    }
}
