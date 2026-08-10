using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderOperatorStatusResolver
{
    public const string PartiallyShippedCode = "PARTIALLY_SHIPPED";

    public static OrderOperatorStatusPresentation Resolve(
        OrderStatus status,
        OrderType type,
        OrderShipmentProgress? shipmentProgress)
    {
        if (type != OrderType.Customer)
        {
            return new OrderOperatorStatusPresentation(
                OrderStatusMapper.StatusToString(status),
                OrderStatusMapper.StatusToDisplayName(status, type));
        }

        if (status is OrderStatus.InProgress or OrderStatus.Accepted
            && shipmentProgress?.IsPartiallyShipped == true)
        {
            return new OrderOperatorStatusPresentation(PartiallyShippedCode, "Частично отгружен");
        }

        return status switch
        {
            OrderStatus.InProgress => new("IN_PROGRESS", "В работе"),
            OrderStatus.Accepted => new("ACCEPTED", "Готов к отгрузке"),
            OrderStatus.Shipped => new("SHIPPED", "Выполнен"),
            OrderStatus.Cancelled => new("CANCELLED", "Отменён"),
            OrderStatus.Draft => new("DRAFT", "Черновик"),
            OrderStatus.Merged => new("MERGED", "Объединён"),
            _ => new("UNKNOWN", "Неизвестно")
        };
    }
}
