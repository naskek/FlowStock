using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using System.Globalization;

namespace FlowStock.Server;

public static class BusinessNotificationEndpoints
{
    public const string WpfReaderKey = "wpf-global";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/notifications", HandleList);
        app.MapGet("/api/notifications/journal", HandleJournal);
        app.MapPost("/api/notifications/{notificationId:long}/read", HandleRead);
        app.MapPost("/api/notifications/read-all", HandleReadAll);
    }

    public static void TryAddFinalizeFailure(IDataStore store, string operationType, long operationId, string? entityRef, Exception exception)
    {
        try
        {
            var reason = exception.GetType().Name;
            var order = store.GetOrder(operationId);
            store.AddBusinessNotification(new BusinessNotification
            {
                EventType = "EXPLICIT_FINALIZE_FAILED",
                Severity = "ERROR",
                Title = operationType == "PRODUCTION_FILLING" ? "Не удалось завершить выпуск" : "Не удалось завершить отгрузку",
                Message = exception.Message,
                EntityType = operationType,
                EntityId = operationId,
                EntityRef = entityRef,
                CreatedAt = DateTime.UtcNow,
                Source = "EXPLICIT_FINALIZE",
                DedupeKey = $"finalize_error:{operationType.ToLowerInvariant()}:{operationId}:{Guid.NewGuid():N}",
                Payload = BusinessNotificationContent.Snapshot(store, order) with { ErrorCode = reason }
            });
        }
        catch
        {
            // A notification failure must not hide the original finalize error.
        }
    }

    private static IResult HandleJournal(HttpRequest request, IDataStore store)
    {
        if (store is not IBusinessNotificationJournalStore journal)
            return Results.Json(new { error = "JOURNAL_UNSUPPORTED" }, statusCode: 503);
        var q = request.Query;
        if (!int.TryParse(q["limit"].FirstOrDefault() ?? "50", out var limit) || limit is < 1 or > 100
            || !int.TryParse(q["offset"].FirstOrDefault() ?? "0", out var offset) || offset < 0
            || !bool.TryParse(q["unreadOnly"].FirstOrDefault() ?? "true", out var unread))
            return Results.BadRequest(new { error = "INVALID_JOURNAL_QUERY" });
        var category = q["category"].FirstOrDefault();
        if (category != null && category is not ("READY" or "SHIPMENT" or "PRODUCTION" or "ERROR" or "OTHER"))
            return Results.BadRequest(new { error = "INVALID_JOURNAL_CATEGORY" });
        if (!TryDate(q["from"].FirstOrDefault(), out var from) || !TryDate(q["to"].FirstOrDefault(), out var to)
            || (from.HasValue && to.HasValue && from >= to) || q["search"].ToString().Length > 200)
            return Results.BadRequest(new { error = "INVALID_JOURNAL_FILTER" });
        var page = journal.GetBusinessNotificationPage(new(unread, limit, offset,
            q["search"].FirstOrDefault(), category, from, to), WpfReaderKey);
        return Results.Ok(new { items = page.Items.Select(Map), total = page.Total, unread_count = page.UnreadCount });
    }

    private static bool TryDate(string? value, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        utc = parsed.UtcDateTime;
        return true;
    }

    private static IResult HandleList(HttpRequest request, IDataStore store)
    {
        var unreadOnly = bool.TryParse(request.Query["unreadOnly"], out var unread) && unread;
        var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? parsedLimit : 100;
        return Results.Ok(store.GetBusinessNotifications(unreadOnly, limit, WpfReaderKey).Select(Map));
    }

    private static IResult HandleRead(long notificationId, IDataStore store)
    {
        store.MarkBusinessNotificationRead(notificationId, WpfReaderKey, DateTime.Now);
        return Results.Ok(new { ok = true });
    }

    private static IResult HandleReadAll(IDataStore store)
    {
        store.MarkAllBusinessNotificationsRead(WpfReaderKey, DateTime.Now);
        return Results.Ok(new { ok = true });
    }

    private static object Map(BusinessNotification notification) => new
    {
        id = notification.Id,
        event_type = notification.EventType,
        severity = notification.Severity,
        title = notification.Title,
        message = notification.Message,
        entity_type = notification.EntityType,
        entity_id = notification.EntityId,
        entity_ref = notification.EntityRef,
        created_at = notification.CreatedAt,
        source = notification.Source,
        is_read = notification.IsRead,
        payload = notification.Payload == null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonSerializer.SerializeToElement(
            notification.Payload, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower })
    };
}
