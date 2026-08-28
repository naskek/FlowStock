using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderDeleteEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapDelete("/api/orders/{orderId:long}", HandleDelete);
    }

    private static IResult HandleDelete(long orderId, IDataStore store)
    {
        var existing = store.GetOrder(orderId);
        if (existing == null)
        {
            return Results.NotFound(new ApiResult(false, "ORDER_NOT_FOUND"));
        }

        var orderService = new OrderService(store);
        try
        {
            orderService.DeleteOrder(orderId);
        }
        catch (OrderMarkingHistoryDeleteException ex)
        {
            return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiResult(false, MapKnownInvalidOperationError(ex, existing.Type)));
        }

        return Results.Ok(new DeleteOrderEnvelope
        {
            Ok = true,
            Result = "DELETED",
            OrderId = existing.Id,
            OrderRef = existing.OrderRef
        });
    }

    private static string MapKnownInvalidOperationError(InvalidOperationException ex, OrderType type)
    {
        if (ex.Message.Contains("Заказ не найден", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_NOT_FOUND";
        }

        if (ex.Message.Contains("только заказ в статусе", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_DELETE_FORBIDDEN_STATUS";
        }

        if (ex.Message.Contains("есть отгрузки или связанные документы", StringComparison.OrdinalIgnoreCase))
        {
            return type == OrderType.Internal
                ? "ORDER_HAS_PRODUCTION_DOCS"
                : "ORDER_HAS_OUTBOUND_DOCS";
        }

        if (ex.Message.Contains("есть отгрузки", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_HAS_SHIPMENTS";
        }

        if (ex.Message.Contains("есть выпуски продукции", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_HAS_PRODUCTION_DOCS";
        }

        if (ex.Message.Contains("уже был выпуск продукции", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER_HAS_PRODUCTION_RECEIPTS";
        }

        return "ORDER_DELETE_FAILED";
    }
}
