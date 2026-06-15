using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Server;

public static class BusinessNotificationEndpoints
{
    public const string WpfReaderKey = "wpf-global";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/notifications", HandleList);
        app.MapPost("/api/notifications/{notificationId:long}/read", HandleRead);
        app.MapPost("/api/notifications/read-all", HandleReadAll);
    }

    public static void TryAddFinalizeFailure(IDataStore store, string operationType, long operationId, string? entityRef, Exception exception)
    {
        try
        {
            var reason = exception.GetType().Name;
            store.AddBusinessNotification(new BusinessNotification
            {
                EventType = "EXPLICIT_FINALIZE_FAILED",
                Severity = "ERROR",
                Title = $"Ошибка завершения операции {entityRef ?? operationId.ToString()}",
                Message = exception.Message,
                EntityType = operationType,
                EntityId = operationId,
                EntityRef = entityRef,
                CreatedAt = DateTime.Now,
                Source = "EXPLICIT_FINALIZE",
                DedupeKey = $"finalize_error:{operationType.ToLowerInvariant()}:{operationId}:{reason}"
            });
        }
        catch
        {
            // A notification failure must not hide the original finalize error.
        }
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
        is_read = notification.IsRead
    };
}
