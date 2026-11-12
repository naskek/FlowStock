using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class BusinessNotificationContent
{
    public static BusinessNotificationPayload Snapshot(IDataStore store, Order? order, Doc? doc = null) => new()
    {
        OrderId = order?.Id, OrderRef = order?.OrderRef,
        OrderType = order == null ? null : OrderStatusMapper.TypeToString(order.Type),
        PartnerName = order?.PartnerName ?? (order?.PartnerId is long id ? store.GetPartner(id)?.Name : null),
        DocId = doc?.Id, DocRef = doc?.DocRef
    };

    public static string Category(BusinessNotification n) => n.Severity == "ERROR" ? "ERROR" : n.EventType switch
    {
        "CUSTOMER_ORDER_READY" => "READY",
        "OUTBOUND_CLOSED" or "CUSTOMER_ORDER_SHIPPED" or "CUSTOMER_ORDER_PARTIALLY_SHIPPED" => "SHIPMENT",
        "INTERNAL_ORDER_COMPLETED" => "PRODUCTION",
        _ => "OTHER"
    };

    public static string Summary(BusinessNotification n)
    {
        var p = n.Payload is { Version: 1 } ? n.Payload : null;
        var orderRef = p?.OrderRef ?? (n.EntityType == "ORDER" ? n.EntityRef : null);
        var order = string.IsNullOrWhiteSpace(orderRef) ? "Заказ" : $"Заказ {orderRef}";
        if (!string.IsNullOrWhiteSpace(p?.PartnerName) && p.OrderType != "INTERNAL") order += $" для «{p.PartnerName}»";
        var doc = p?.DocRef ?? (n.EntityType == "DOC" ? n.EntityRef : null);
        return n.EventType switch
        {
            "CUSTOMER_ORDER_READY" => $"{order} готов к отгрузке",
            "CUSTOMER_ORDER_SHIPPED" => $"{order} полностью отгружен",
            "CUSTOMER_ORDER_PARTIALLY_SHIPPED" => $"{order} частично отгружен",
            "INTERNAL_ORDER_COMPLETED" => $"Внутренний заказ {orderRef ?? "без сохранённого номера"} выполнен",
            "OUTBOUND_CLOSED" when orderRef != null => $"По заказу {orderRef}{(p?.PartnerName is { Length: > 0 } name ? $" для «{name}»" : "")} проведена отгрузка {doc}",
            "OUTBOUND_CLOSED" => doc == null ? "Отгрузка проведена" : $"Отгрузка {doc} проведена",
            "EXPLICIT_FINALIZE_FAILED" => $"Не удалось завершить {(n.EntityType == "PRODUCTION_FILLING" ? "выпуск" : "отгрузку")}{(orderRef == null ? "" : $" по заказу {orderRef}")}",
            _ => orderRef == null ? "Событие склада" : $"Событие по заказу {orderRef}"
        };
    }
}
