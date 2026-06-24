using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class OrderControlEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/order-control/preview", HandlePreview);
        app.MapPost("/api/order-control/tasks", HandleCreate);
        app.MapGet("/api/order-control/tasks", HandleList);
        app.MapGet("/api/order-control/tasks/{id:long}", HandleGet);
        app.MapGet("/api/order-control/tasks/{id:long}/progress", HandleProgress);
        app.MapPost("/api/order-control/tasks/{id:long}/cancel", HandleCancel);

        app.MapGet("/api/tsd/order-control/tasks", HandleTsdList);
        app.MapGet("/api/tsd/order-control/tasks/{id:long}", HandleTsdGet);
        app.MapPost("/api/tsd/order-control/tasks/{id:long}/start", HandleTsdStart);
        app.MapPost("/api/tsd/order-control/tasks/{id:long}/scan", HandleTsdScan);
        app.MapPost("/api/tsd/order-control/tasks/{id:long}/complete", HandleTsdComplete);
    }

    private static IResult HandlePreview(OrderControlPreviewRequest request, OrderControlService service)
    {
        var preview = service.Preview(request.OrderIds ?? Array.Empty<long>());
        return Results.Ok(MapPreview(preview));
    }

    private static IResult HandleCreate(OrderControlCreateRequest request, OrderControlService service)
    {
        var result = service.Create(request.OrderIds ?? Array.Empty<long>(), request.CreatedBy, request.Comment);
        if (!result.Success)
        {
            return Results.BadRequest(new { ok = false, error = result.ErrorCode, message = result.Message });
        }

        return Results.Ok(new
        {
            ok = true,
            message = result.Message,
            task = result.Task == null ? null : MapDetails(result.Task)
        });
    }

    private static IResult HandleList(string? status, bool activeOnly, OrderControlService service)
    {
        return Results.Ok(service.GetTasks(status, activeOnly).Select(MapSummary).ToArray());
    }

    private static IResult HandleGet(long id, OrderControlService service)
    {
        var details = service.GetDetails(id);
        return details == null
            ? Results.NotFound(new { ok = false, error = OrderControlErrorCodes.TaskNotFound, message = "Задание не найдено." })
            : Results.Ok(MapDetails(details));
    }

    private static IResult HandleProgress(long id, OrderControlService service)
    {
        var details = service.GetDetails(id);
        return details == null
            ? Results.NotFound(new { ok = false, error = OrderControlErrorCodes.TaskNotFound, message = "Задание не найдено." })
            : Results.Ok(MapProgress(details));
    }

    private static IResult HandleCancel(long id, OrderControlCancelRequest request, OrderControlService service)
    {
        var details = service.Cancel(id, request.CancelledBy);
        return details == null
            ? Results.NotFound(new { ok = false, error = OrderControlErrorCodes.TaskNotFound, message = "Задание не найдено." })
            : Results.Ok(new { ok = true, task = MapDetails(details) });
    }

    private static IResult HandleTsdList(OrderControlService service)
    {
        return Results.Ok(service.GetTasks(null, activeOnly: true).Select(MapSummary).ToArray());
    }

    private static IResult HandleTsdGet(long id, OrderControlService service) => HandleGet(id, service);

    private static IResult HandleTsdStart(long id, OrderControlTsdDeviceRequest request, OrderControlService service)
    {
        var details = service.Start(id, request.DeviceId, request.OperatorId);
        return details == null
            ? Results.NotFound(new { ok = false, error = OrderControlErrorCodes.TaskNotFound, message = "Задание не найдено." })
            : Results.Ok(new { ok = true, task = MapDetails(details) });
    }

    private static IResult HandleTsdScan(long id, OrderControlScanRequest request, OrderControlService service)
    {
        var result = service.Scan(id, request.HuCode, request.RequestId, request.DeviceId, request.OperatorId);
        if (!result.Success)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = result.ErrorCode,
                message = result.Message,
                task = result.Task == null ? null : MapDetails(result.Task)
            });
        }

        return Results.Ok(new
        {
            ok = true,
            message = result.Message,
            already_checked = result.AlreadyChecked,
            task = result.Task == null ? null : MapDetails(result.Task)
        });
    }

    private static IResult HandleTsdComplete(long id, OrderControlTsdDeviceRequest request, OrderControlService service)
    {
        var result = service.Complete(id, request.DeviceId, request.OperatorId);
        if (!result.Success)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = result.ErrorCode,
                message = result.Message,
                task = result.Task == null ? null : MapDetails(result.Task)
            });
        }

        return Results.Ok(new
        {
            ok = true,
            message = result.Message,
            task = result.Task == null ? null : MapDetails(result.Task)
        });
    }

    private static object MapPreview(OrderControlPreviewResult preview) => new
    {
        can_create = preview.CanCreate,
        error = preview.ErrorCode,
        message = preview.Message,
        orders = preview.Orders.Select(order => new
        {
            order_id = order.OrderId,
            order_ref = order.OrderRef,
            partner_name = order.PartnerName,
            is_eligible = order.IsEligible,
            error = order.ErrorCode,
            message = order.Message
        }).ToArray(),
        hu_count = preview.Hus.Count,
        hus = preview.Hus.Select(hu => new
        {
            hu_code = hu.HuCode,
            order_refs = hu.OrderRefs,
            item_summary = hu.ItemSummary,
            location_code = hu.LocationCode,
            source_type = hu.SourceType,
            qty = hu.Qty,
            is_mixed_pallet = hu.Lines.Count > 1,
            lines = hu.Lines.Select(MapHuLine).ToArray()
        }).ToArray(),
        warnings = preview.Warnings
    };

    private static object MapSummary(OrderControlTaskSummary summary) => new
    {
        id = summary.Task.Id,
        task_ref = summary.Task.TaskRef,
        status = summary.Task.Status,
        status_display = MapTaskStatus(summary.Task.Status),
        created_at = summary.Task.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        expected_hu_count = summary.Task.ExpectedHuCount,
        checked_hu_count = summary.Task.CheckedHuCount,
        discrepancy_hu_count = summary.Task.DiscrepancyHuCount,
        is_complete = summary.Task.ExpectedHuCount > 0 && summary.Task.CheckedHuCount >= summary.Task.ExpectedHuCount,
        orders = summary.Orders.Select(order => new
        {
            order_id = order.OrderId,
            order_ref = order.OrderRef,
            partner_name = order.PartnerName
        }).ToArray()
    };

    private static object MapDetails(OrderControlTaskDetails details) => new
    {
        task = MapSummary(new OrderControlTaskSummary { Task = details.Task, Orders = details.Orders }),
        progress = MapProgress(details),
        hus = details.Hus.Select(hu => new
        {
            id = hu.Id,
            hu_code = hu.HuCode,
            normalized_hu = hu.NormalizedHu,
            status = hu.Status,
            qty = hu.Qty,
            item_summary = hu.ItemSummary,
            checked_at = hu.CheckedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            checked_by_device_id = hu.CheckedByDeviceId,
            checked_by_operator = hu.CheckedByOperator,
            error = hu.ErrorCode,
            message = hu.ErrorMessage,
            is_mixed_pallet = details.HuLines.Count(line => line.TaskHuId == hu.Id) > 1,
            lines = details.HuLines.Where(line => line.TaskHuId == hu.Id).Select(MapHuLine).ToArray()
        }).ToArray(),
        events = details.Events.Select(e => new
        {
            id = e.Id,
            event_type = e.EventType,
            event_at = e.EventAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            device_id = e.DeviceId,
            operator_id = e.OperatorId,
            hu_code = e.HuCode,
            request_id = e.RequestId,
            error = e.ErrorCode,
            message = e.Message
        }).ToArray()
    };

    private static object MapProgress(OrderControlTaskDetails details) => new
    {
        expected_hu_count = details.Task.ExpectedHuCount,
        checked_hu_count = details.Task.CheckedHuCount,
        discrepancy_hu_count = details.Task.DiscrepancyHuCount,
        pending_hu_count = Math.Max(0, details.Task.ExpectedHuCount - details.Task.CheckedHuCount - details.Task.DiscrepancyHuCount),
        can_complete = details.Task.ExpectedHuCount > 0
                       && details.Task.CheckedHuCount >= details.Task.ExpectedHuCount
                       && details.Task.DiscrepancyHuCount == 0
                       && OrderControlTaskStatus.IsActive(details.Task.Status)
    };

    private static object MapHuLine(OrderControlTaskHuLine line) => new
    {
        order_id = line.OrderId,
        order_ref = line.OrderRef,
        order_line_id = line.OrderLineId,
        item_id = line.ItemId,
        item_name = line.ItemName,
        qty = line.Qty,
        location_id = line.LocationId,
        location_code = line.LocationCode,
        source_type = line.SourceType
    };

    private static string MapTaskStatus(string status)
    {
        return status switch
        {
            OrderControlTaskStatus.New => "Новая",
            OrderControlTaskStatus.InExecution => "В работе",
            OrderControlTaskStatus.Completed => "Завершена",
            OrderControlTaskStatus.Cancelled => "Отменена",
            _ => status
        };
    }

    private sealed class OrderControlPreviewRequest
    {
        [JsonPropertyName("order_ids")]
        public long[]? OrderIds { get; init; }
    }

    private sealed class OrderControlCreateRequest
    {
        [JsonPropertyName("order_ids")]
        public long[]? OrderIds { get; init; }

        [JsonPropertyName("created_by")]
        public string? CreatedBy { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }
    }

    private sealed class OrderControlCancelRequest
    {
        [JsonPropertyName("cancelled_by")]
        public string? CancelledBy { get; init; }
    }

    private sealed class OrderControlScanRequest
    {
        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("operator_id")]
        public string? OperatorId { get; init; }
    }

    private sealed class OrderControlTsdDeviceRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("operator_id")]
        public string? OperatorId { get; init; }
    }
}
