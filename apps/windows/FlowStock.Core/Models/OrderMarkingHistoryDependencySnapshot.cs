namespace FlowStock.Core.Models;

public sealed record OrderMarkingHistoryDependencySnapshot(
    bool HasOrderHistory,
    IReadOnlySet<long> OrderLineIds)
{
    public bool BlocksOrderDelete => HasOrderHistory || OrderLineIds.Count > 0;

    public bool BlocksOrderLineDelete(long orderLineId) => OrderLineIds.Contains(orderLineId);
}

public sealed class OrderMarkingHistoryDeleteException : InvalidOperationException
{
    public const string OrderErrorCode = "ORDER_MARKING_HISTORY_DELETE_FORBIDDEN";
    public const string OrderLineErrorCode = "ORDER_LINE_MARKING_HISTORY_DELETE_FORBIDDEN";

    public OrderMarkingHistoryDeleteException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }

    public static OrderMarkingHistoryDeleteException ForOrder() => new(
        OrderErrorCode,
        "Заказ имеет историю маркировки и не может быть физически удалён. Отмените заказ и сохраните его как историю.");

    public static OrderMarkingHistoryDeleteException ForOrderLine() => new(
        OrderLineErrorCode,
        "Строка заказа имеет историю маркировки и не может быть физически удалена. Отмените строку логически и сохраните историю.");
}
