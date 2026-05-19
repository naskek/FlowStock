using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services.Warehouse;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class WarehouseTaskEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/tsd/tasks", HandleList);
        app.MapGet("/api/tsd/tasks/{id:long}", HandleGet);
        app.MapPost("/api/tsd/tasks/{id:long}/start", HandleStart);
        app.MapPost("/api/tsd/tasks/{id:long}/scan", HandleScan);
        app.MapPost("/api/tsd/tasks/{id:long}/complete", HandleComplete);
    }

    private static IResult HandleList(string? deviceId, WarehouseTaskExecutionService service, IDataStore store)
    {
        var tasks = service.ListActiveTasks(deviceId);
        return Results.Ok(new
        {
            success = true,
            tasks = tasks.Select(task =>
            {
                var lines = store.GetWarehouseTaskLines(task.Id);
                var firstLine = lines.FirstOrDefault();
                return new
                {
                    id = task.Id,
                    task_ref = task.TaskRef,
                    bundle_id = task.BundleId,
                    task_type = task.TaskType,
                    status = task.Status,
                    expected_hu_code = firstLine?.ExpectedHuCode,
                    to_location_id = firstLine?.ToLocationId,
                    created_at = task.CreatedAt
                };
            }).ToArray()
        });
    }

    private static IResult HandleGet(long id, IDataStore store)
    {
        var task = store.GetWarehouseTask(id);
        if (task == null)
        {
            return Results.NotFound(new { success = false, error = "TASK_NOT_FOUND", message = "Задание не найдено." });
        }

        return Results.Ok(new
        {
            success = true,
            task = new
            {
                id = task.Id,
                task_ref = task.TaskRef,
                bundle_id = task.BundleId,
                task_type = task.TaskType,
                status = task.Status,
                assigned_to_device_id = task.AssignedToDeviceId,
                created_at = task.CreatedAt,
                started_at = task.StartedAt,
                executed_at = task.ExecutedAt
            },
            lines = store.GetWarehouseTaskLines(id).Select(line => new
            {
                id = line.Id,
                line_no = line.LineNo,
                expected_hu_code = line.ExpectedHuCode,
                to_location_id = line.ToLocationId,
                status = line.Status,
                scanned_hu_code = line.ScannedHuCode,
                scanned_location_id = line.ScannedLocationId
            }),
            events = store.GetWarehouseTaskEvents(id).Select(e => new
            {
                id = e.Id,
                event_type = e.EventType,
                event_at = e.EventAt,
                hu_code = e.HuCode,
                location_id = e.LocationId,
                message = e.Message
            })
        });
    }

    private static async Task<IResult> HandleStart(long id, HttpRequest request, WarehouseTaskExecutionService service)
    {
        var body = await request.ReadFromJsonAsync<TaskDeviceRequest>();
        return MapResult(service.StartTask(id, body?.DeviceId, body?.OperatorId));
    }

    private static async Task<IResult> HandleScan(long id, HttpRequest request, WarehouseTaskExecutionService service)
    {
        var body = await request.ReadFromJsonAsync<ScanRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Barcode))
        {
            return Results.BadRequest(new { success = false, error = "MISSING_BARCODE" });
        }

        return MapResult(service.Scan(
            id,
            body.Barcode,
            body.ScanType ?? "HU",
            body.DeviceId,
            body.OperatorId));
    }

    private static async Task<IResult> HandleComplete(long id, HttpRequest request, WarehouseTaskExecutionService service)
    {
        var body = await request.ReadFromJsonAsync<TaskDeviceRequest>();
        return MapResult(service.CompleteTask(id, body?.DeviceId, body?.OperatorId));
    }

    private static IResult MapResult(WarehouseBundleOperationResult result)
    {
        if (!result.Success)
        {
            var error = result.Errors.FirstOrDefault();
            return Results.BadRequest(new
            {
                success = false,
                error = error?.Code ?? "OPERATION_FAILED",
                message = error?.Message ?? "Операция не выполнена.",
                errors = result.Errors.Select(e => new { code = e.Code, message = e.Message })
            });
        }

        return Results.Ok(new
        {
            success = true,
            bundle_id = result.BundleId,
            status = result.Status,
            message = result.Message
        });
    }

    private sealed class TaskDeviceRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("operator_id")]
        public string? OperatorId { get; init; }
    }

    private sealed class ScanRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("operator_id")]
        public string? OperatorId { get; init; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; init; }

        [JsonPropertyName("scan_type")]
        public string? ScanType { get; init; }
    }
}
