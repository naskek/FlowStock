using System.Globalization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.App;

public sealed class BusinessNotificationRow
{
    public BusinessNotificationRow(BusinessNotification notification) { Notification = notification; }
    public BusinessNotification Notification { get; }
    public long Id => Notification.Id;
    private BusinessNotificationPayload? Snapshot => Notification.Payload is { Version: 1 } p ? p : null;
    public string Summary => BusinessNotificationContent.Summary(Notification);
    public string Category => BusinessNotificationContent.Category(Notification);
    public string CategoryLabel => Category switch
    {
        "READY" => "Готовность заказа", "SHIPMENT" => "Отгрузка", "PRODUCTION" => "Производство",
        "ERROR" => "Ошибка операции", _ => "Другое событие"
    };
    public string Marker => Category == "ERROR" ? "!" : Category == "READY" ? "✓" : "•";
    public bool IsUnread => !Notification.IsRead;
    public string ReadLabel => IsUnread ? "Новое" : "Просмотрено";
    public static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
    public string TimeDisplay => Notification.CreatedAt == DateTime.MinValue ? "Время не сохранено"
        : AsUtc(Notification.CreatedAt).ToLocalTime().ToString("dd.MM.yyyy · HH:mm", CultureInfo.InvariantCulture);
    public string TimeTooltip => Notification.CreatedAt == DateTime.MinValue ? "Время не сохранено"
        : new DateTimeOffset(AsUtc(Notification.CreatedAt)).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)
          + " · " + TimeZoneInfo.Local.DisplayName;
    public string Subtitle => $"{TimeDisplay} · {CategoryLabel}";
    public long? OrderId => Snapshot?.OrderId ?? (Notification.EntityType == "ORDER" ? Notification.EntityId : null);
    public long? DocId => Snapshot?.DocId ?? (Notification.EntityType == "DOC" ? Notification.EntityId : null);
    public string OrderDisplay => Snapshot?.OrderRef ?? (Notification.EntityType == "ORDER" ? Notification.EntityRef : null) ?? "Номер заказа не сохранён";
    public string PartnerDisplay => Snapshot?.PartnerName ?? "Клиент на момент события не сохранён";
    public string DocDisplay => Snapshot?.DocRef ?? (Notification.EntityType == "DOC" ? Notification.EntityRef : null) ?? "Связанный документ не сохранён";
    public string Details => Category == "ERROR"
        ? "Проверьте фактический результат операции. Подробности ошибки доступны в диагностике."
        : Notification.EventType == "INTERNAL_ORDER_COMPLETED" ? "Производство на склад завершено."
        : Snapshot?.ShipmentLines is { Count: > 0 } lines
            ? string.Join("\n", lines.Select(line => $"{line.ItemName}: отгружено {line.Shipped:G} из {line.Ordered:G}, осталось {line.Remaining:G}"))
            : "Событие сохранено сервером. Количества на момент события не записаны.";
    public string Diagnostics => $"Тип: {Notification.EventType}\nИсточник: {Notification.Source}\nКод: {Snapshot?.ErrorCode ?? "—"}\n\nИсходный заголовок: {Notification.Title}\nИсходное описание: {Notification.Message}";
}
