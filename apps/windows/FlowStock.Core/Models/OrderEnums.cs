namespace FlowStock.Core.Models;

public enum OrderType
{
    Customer,
    Internal
}

public enum OrderStatus
{
    Draft,
    Accepted,
    InProgress,
    Shipped
}

public static class OrderStatusMapper
{
    public static OrderType? TypeFromString(string? type)
    {
        return type?.Trim().ToUpperInvariant() switch
        {
            "CUSTOMER" => OrderType.Customer,
            "INTERNAL" => OrderType.Internal,
            _ => null
        };
    }

    public static string TypeToString(OrderType type)
    {
        return type switch
        {
            OrderType.Customer => "CUSTOMER",
            OrderType.Internal => "INTERNAL",
            _ => "CUSTOMER"
        };
    }

    public static string TypeToDisplayName(OrderType type)
    {
        return type switch
        {
            OrderType.Customer => "Клиентский",
            OrderType.Internal => "Внутренний выпуск",
            _ => "Неизвестно"
        };
    }

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
        return StatusToDisplayName(status, OrderType.Customer);
    }

    public static string StatusToDisplayName(OrderStatus status, OrderType type)
    {
        return status switch
        {
            OrderStatus.Draft => "Черновик",
            OrderStatus.Accepted => "Готов к отгрузке",
            OrderStatus.InProgress => "В работе",
            OrderStatus.Shipped => type == OrderType.Internal ? "Завершен" : "Отгружен",
            _ => "Неизвестно"
        };
    }
}

