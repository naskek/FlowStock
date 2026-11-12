using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.App;

public partial class IncomingRequestsWindow
{
    public IncomingRequestsWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _isPreview = true;
        InitializeComponent();
        Title += " [UI Preview / DEV — без БД и API]";
        RequestsGrid.ItemsSource = _rows;
        NotificationsGrid.ItemsSource = _notificationRows;
        EventJournalTab.IsSelected = true;
        ApproveButton.IsEnabled = RejectButton.IsEnabled = DetailsButton.IsEnabled = false;
        var now = DateTime.UtcNow;
        var types = new[] { "CUSTOMER_ORDER_READY", "CUSTOMER_ORDER_PARTIALLY_SHIPPED", "CUSTOMER_ORDER_SHIPPED", "INTERNAL_ORDER_COMPLETED", "EXPLICIT_FINALIZE_FAILED", "CUSTOMER_ORDER_READY" };
        _previewNotifications = types.Select((type, i) => new BusinessNotification
        {
            Id = i + 1, EventType = type, EntityType = type == "EXPLICIT_FINALIZE_FAILED" ? "OUTBOUND" : "ORDER",
            EntityId = 40 + i, EntityRef = $"SO-2026-{42 + i:000}", CreatedAt = now.AddMinutes(-i * 17),
            Severity = type == "EXPLICIT_FINALIZE_FAILED" ? "ERROR" : "INFO", IsRead = i == 3,
            Title = "Исторический заголовок", Message = type == "EXPLICIT_FINALIZE_FAILED" ? "Пример диагностического сообщения" : "",
            Payload = i == 5 ? null : new BusinessNotificationPayload
            {
                OrderId = 40 + i, OrderRef = $"SO-2026-{42 + i:000}",
                PartnerName = i == 1 ? "ООО «Продуктовая логистика — Северо-Западный регион»" : "Тестовый клиент",
                OrderType = i == 3 ? "INTERNAL" : "CUSTOMER", DocId = i is 1 or 2 ? 100 + i : null,
                DocRef = i is 1 or 2 ? $"OUT-2026-{100 + i}" : null,
                ShipmentLines = i == 1 ? new[] { new BusinessNotificationQuantity("Горчица, 200 г", 2400, 1200, 1200) } : null
            }
        }).ToArray();
        Closed += (_, _) => { _windowClosed = true; _journalCancellation?.Cancel(); };
        _journalReady = true;
        LoadNotifications();
    }

    private BusinessNotificationPage PreviewPage(BusinessNotificationQuery query)
    {
        var rows = _previewNotifications.Where(n => (!query.UnreadOnly || !n.IsRead)
            && (query.Category == null || BusinessNotificationContent.Category(n) == query.Category)
            && (query.FromUtc == null || n.CreatedAt >= query.FromUtc)
            && (query.ToUtc == null || n.CreatedAt < query.ToUtc)
            && (string.IsNullOrWhiteSpace(query.Search) || BusinessNotificationContent.Summary(n).Contains(query.Search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        return new BusinessNotificationPage { Items = rows.Skip(query.Offset).Take(query.Limit).ToArray(), Total = rows.Length, UnreadCount = _previewNotifications.Count(n => !n.IsRead) };
    }
}
