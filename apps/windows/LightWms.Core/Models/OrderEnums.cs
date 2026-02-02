namespace LightWms.Core.Models;

public enum OrderStatus
{
    Draft,
    Accepted,
    InProgress,
    Shipped
}

public static class OrderStatusMapper
{
    public static OrderStatus? StatusFromString(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "DRAFT" => OrderStatus.Draft,
            "ACCEPTED" => OrderStatus.Accepted,
            "IN_PROGRESS" => OrderStatus.InProgress,
            "SHIPPED" => OrderStatus.Shipped,
            _ => null
        };
    }

    public static string StatusToString(OrderStatus status)
    {
        return status switch
        {
            OrderStatus.Draft => "DRAFT",
            OrderStatus.Accepted => "ACCEPTED",
            OrderStatus.InProgress => "IN_PROGRESS",
            OrderStatus.Shipped => "SHIPPED",
            _ => "UNKNOWN"
        };
    }

    public static string StatusToDisplayName(OrderStatus status)
    {
        return status switch
        {
            OrderStatus.Draft => "Черновик",
            OrderStatus.Accepted => "Принят",
            OrderStatus.InProgress => "В процессе",
            OrderStatus.Shipped => "Отгружен",
            _ => "Неизвестно"
        };
    }
}
