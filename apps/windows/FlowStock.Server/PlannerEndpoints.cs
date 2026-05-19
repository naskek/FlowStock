using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services.Warehouse;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class PlannerEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/planner/bundles/preview", HandlePreviewDraft);
        app.MapPost("/api/planner/bundles", HandleCreate);
        app.MapGet("/api/planner/bundles", HandleList);
        app.MapGet("/api/planner/bundles/{id:long}", HandleGet);
        app.MapPost("/api/planner/bundles/{id:long}/lines", HandleAddLine);
        app.MapPost("/api/planner/bundles/{id:long}/submit", HandleSubmit);
        app.MapPost("/api/planner/bundles/{id:long}/approve", HandleApprove);
        app.MapPost("/api/planner/bundles/{id:long}/reject", HandleReject);
        app.MapPost("/api/planner/bundles/{id:long}/confirm-execution", HandleConfirmExecution);
        app.MapPost("/api/planner/bundles/{id:long}/cancel", HandleCancel);
    }

    private static IResult HandlePreviewDraft(PreviewDraftRequest? body, WarehouseActionBundleService service)
    {
        if (body?.Lines == null || body.Lines.Count == 0)
        {
            return Results.BadRequest(new { success = false, error = "EMPTY_LINES", message = "Укажите хотя бы одно действие." });
        }

        var preview = service.PreviewLines(body.Lines.Select(MapLineInput).ToArray());
        return Results.Ok(MapPreview(preview));
    }

    private static IResult HandleCreate(CreateBundleRequest? body, WarehouseActionBundleService service)
    {
        var result = service.CreateBundle(
            body?.Source ?? WarehouseBundleSource.Wpf,
            body?.CreatedBy,
            body?.Comment);
        return MapOperationResult(result);
    }

    private static IResult HandleList(string? status, IDataStore store)
    {
        var bundles = store.GetWarehouseActionBundles(NormalizeListStatus(status));
        return Results.Ok(new
        {
            success = true,
            bundles = bundles.Select(MapBundleSummary).ToArray()
        });
    }

    private static IResult HandleGet(long id, IDataStore store)
    {
        var bundle = store.GetWarehouseActionBundle(id);
        if (bundle == null)
        {
            return Results.NotFound(new { success = false, error = "BUNDLE_NOT_FOUND", message = "Пакет не найден." });
        }

        var lines = store.GetWarehouseActionLines(id);
        var tasks = store.GetWarehouseTasksByBundle(id);
        var taskDetails = tasks.Select(task =>
        {
            var taskLines = store.GetWarehouseTaskLines(task.Id);
            var events = store.GetWarehouseTaskEvents(task.Id);
            return new
            {
                task = MapTask(task),
                lines = taskLines.Select(MapTaskLine).ToArray(),
                events = events.Select(MapEvent).ToArray()
            };
        }).ToArray();

        return Results.Ok(new
        {
            success = true,
            bundle = MapBundle(bundle),
            lines = lines.Select(MapLine).ToArray(),
            tasks = taskDetails
        });
    }

    private static async Task<IResult> HandleAddLine(
        long id,
        HttpRequest request,
        WarehouseActionBundleService service)
    {
        var body = await ReadJson<AddLineRequest>(request);
        if (body == null)
        {
            return Results.BadRequest(new { success = false, error = "INVALID_JSON" });
        }

        return MapOperationResult(service.AddLine(id, MapLineInput(body)));
    }

    private static async Task<IResult> HandleSubmit(long id, HttpRequest request, WarehouseActionBundleService service)
    {
        var body = await ReadJson<ActorRequest>(request);
        return MapOperationResult(service.SubmitBundle(id, body?.Actor));
    }

    private static async Task<IResult> HandleApprove(long id, HttpRequest request, WarehouseActionBundleService service)
    {
        var body = await ReadJson<ActorRequest>(request);
        return MapOperationResult(service.ApproveBundle(id, body?.Actor ?? body?.ApprovedBy));
    }

    private static async Task<IResult> HandleReject(long id, HttpRequest request, WarehouseActionBundleService service)
    {
        var body = await ReadJson<RejectRequest>(request);
        return MapOperationResult(service.RejectBundle(id, body?.Actor ?? body?.RejectedBy, body?.Comment));
    }

    private static async Task<IResult> HandleConfirmExecution(long id, HttpRequest request, WarehouseActionBundleService service)
    {
        var body = await ReadJson<ActorRequest>(request);
        return MapOperationResult(service.ConfirmExecution(id, body?.Actor));
    }

    private static IResult HandleCancel(long id, WarehouseActionBundleService service)
    {
        return MapOperationResult(service.CancelBundle(id));
    }

    private static IResult MapOperationResult(WarehouseBundleOperationResult result)
    {
        if (!result.Success)
        {
            return Results.BadRequest(new
            {
                success = false,
                errors = result.Errors.Select(error => new { code = error.Code, message = error.Message, line_no = error.LineNo }).ToArray(),
                warnings = result.Warnings.Select(warning => new { code = warning.Code, message = warning.Message, line_no = warning.LineNo }).ToArray()
            });
        }

        return Results.Ok(new
        {
            success = true,
            bundle_id = result.BundleId,
            bundle_ref = result.BundleRef,
            status = result.Status,
            message = result.Message
        });
    }

    private static object MapPreview(WarehouseBundlePreviewResult preview) => new
    {
        success = true,
        valid = preview.Valid,
        errors = preview.Errors.Select(e => new { code = e.Code, message = e.Message, line_no = e.LineNo }),
        warnings = preview.Warnings.Select(w => new { code = w.Code, message = w.Message, line_no = w.LineNo }),
        lines = preview.Lines.Select(l => new { line_no = l.LineNo, action_type = l.ActionType, summary = l.Summary })
    };

    private static object MapBundleSummary(WarehouseActionBundle bundle) => new
    {
        id = bundle.Id,
        bundle_ref = bundle.BundleRef,
        source = bundle.Source,
        status = bundle.Status,
        created_at = bundle.CreatedAt,
        created_by = bundle.CreatedBy,
        approved_at = bundle.ApprovedAt,
        executed_at = bundle.ExecutedAt,
        completed_at = bundle.CompletedAt,
        comment = bundle.Comment
    };

    private static object MapBundle(WarehouseActionBundle bundle) => MapBundleSummary(bundle);

    private static object MapLine(WarehouseActionLine line) => new
    {
        id = line.Id,
        line_no = line.LineNo,
        action_type = line.ActionType,
        status = line.Status,
        source_order_id = line.SourceOrderId,
        target_order_id = line.TargetOrderId,
        source_doc_id = line.SourceDocId,
        target_doc_id = line.TargetDocId,
        item_id = line.ItemId,
        hu_code = line.HuCode,
        from_location_id = line.FromLocationId,
        to_location_id = line.ToLocationId,
        qty = line.Qty,
        payload_json = line.PayloadJson,
        result_json = line.ResultJson,
        error_code = line.ErrorCode,
        error_message = line.ErrorMessage
    };

    private static object MapTask(WarehouseTask task) => new
    {
        id = task.Id,
        task_ref = task.TaskRef,
        bundle_id = task.BundleId,
        action_line_id = task.ActionLineId,
        task_type = task.TaskType,
        status = task.Status,
        assigned_to_device_id = task.AssignedToDeviceId,
        created_at = task.CreatedAt,
        started_at = task.StartedAt,
        executed_at = task.ExecutedAt,
        confirmed_at = task.ConfirmedAt
    };

    private static object MapTaskLine(WarehouseTaskLine line) => new
    {
        id = line.Id,
        line_no = line.LineNo,
        expected_hu_code = line.ExpectedHuCode,
        expected_item_id = line.ExpectedItemId,
        expected_qty = line.ExpectedQty,
        from_location_id = line.FromLocationId,
        to_location_id = line.ToLocationId,
        status = line.Status,
        scanned_hu_code = line.ScannedHuCode,
        scanned_location_id = line.ScannedLocationId,
        scanned_at = line.ScannedAt
    };

    private static object MapEvent(WarehouseTaskEvent warehouseEvent) => new
    {
        id = warehouseEvent.Id,
        event_type = warehouseEvent.EventType,
        event_at = warehouseEvent.EventAt,
        hu_code = warehouseEvent.HuCode,
        location_id = warehouseEvent.LocationId,
        message = warehouseEvent.Message
    };

    private static WarehouseBundleLineInput MapLineInput(AddLineRequest request) => new()
    {
        ActionType = request.ActionType ?? string.Empty,
        PayloadJson = request.PayloadJson ?? "{}",
        SourceOrderId = request.SourceOrderId,
        TargetOrderId = request.TargetOrderId,
        ItemId = request.ItemId,
        HuCode = request.HuCode,
        FromLocationId = request.FromLocationId,
        ToLocationId = request.ToLocationId,
        Qty = request.Qty
    };

    private static WarehouseBundleLineInput MapLineInput(PreviewLineRequest request) => new()
    {
        ActionType = request.ActionType ?? string.Empty,
        PayloadJson = request.PayloadJson ?? "{}",
        SourceOrderId = request.SourceOrderId,
        TargetOrderId = request.TargetOrderId,
        ItemId = request.ItemId,
        HuCode = request.HuCode,
        FromLocationId = request.FromLocationId,
        ToLocationId = request.ToLocationId,
        Qty = request.Qty
    };

    private static string? NormalizeListStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return status.Trim().ToUpperInvariant();
    }

    private static async Task<T?> ReadJson<T>(HttpRequest request)
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private sealed class CreateBundleRequest
    {
        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("created_by")]
        public string? CreatedBy { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }
    }

    private sealed class PreviewDraftRequest
    {
        [JsonPropertyName("lines")]
        public List<PreviewLineRequest>? Lines { get; init; }
    }

    private sealed class PreviewLineRequest
    {
        [JsonPropertyName("action_type")]
        public string? ActionType { get; init; }

        [JsonPropertyName("payload_json")]
        public string? PayloadJson { get; init; }

        [JsonPropertyName("source_order_id")]
        public long? SourceOrderId { get; init; }

        [JsonPropertyName("target_order_id")]
        public long? TargetOrderId { get; init; }

        [JsonPropertyName("item_id")]
        public long? ItemId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("from_location_id")]
        public long? FromLocationId { get; init; }

        [JsonPropertyName("to_location_id")]
        public long? ToLocationId { get; init; }

        [JsonPropertyName("qty")]
        public double? Qty { get; init; }
    }

    private sealed class AddLineRequest
    {
        [JsonPropertyName("action_type")]
        public string? ActionType { get; init; }

        [JsonPropertyName("payload_json")]
        public string? PayloadJson { get; init; }

        [JsonPropertyName("source_order_id")]
        public long? SourceOrderId { get; init; }

        [JsonPropertyName("target_order_id")]
        public long? TargetOrderId { get; init; }

        [JsonPropertyName("item_id")]
        public long? ItemId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("from_location_id")]
        public long? FromLocationId { get; init; }

        [JsonPropertyName("to_location_id")]
        public long? ToLocationId { get; init; }

        [JsonPropertyName("qty")]
        public double? Qty { get; init; }
    }

    private sealed class ActorRequest
    {
        [JsonPropertyName("actor")]
        public string? Actor { get; init; }

        [JsonPropertyName("approved_by")]
        public string? ApprovedBy { get; init; }
    }

    private sealed class RejectRequest
    {
        [JsonPropertyName("actor")]
        public string? Actor { get; init; }

        [JsonPropertyName("rejected_by")]
        public string? RejectedBy { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }
    }
}
