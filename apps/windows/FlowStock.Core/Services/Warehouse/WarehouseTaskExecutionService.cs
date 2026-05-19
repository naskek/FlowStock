using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Warehouse;

public sealed class WarehouseTaskExecutionService
{
    private readonly IDataStore _data;
    private readonly WarehouseActionBundleService _bundles;

    public WarehouseTaskExecutionService(IDataStore data)
    {
        _data = data;
        _bundles = new WarehouseActionBundleService(data);
    }

    public WarehouseTask? GetTask(long taskId) => _data.GetWarehouseTask(taskId);

    public IReadOnlyList<WarehouseTask> ListActiveTasks(string? deviceId) =>
        _data.GetActiveWarehouseTasks(deviceId);

    public WarehouseBundleOperationResult StartTask(long taskId, string? deviceId, string? operatorId = null)
    {
        var task = RequireTask(taskId);
        if (task.Status is WarehouseTaskStatus.Executed or WarehouseTaskStatus.Confirmed or WarehouseTaskStatus.Cancelled)
        {
            return Fail("TASK_ALREADY_COMPLETED", "Задание уже завершено.");
        }

        var now = DateTime.Now;
        _data.UpdateWarehouseTaskStatus(
            taskId,
            WarehouseTaskStatus.InExecution,
            now,
            null,
            null,
            null,
            deviceId,
            operatorId);

        return WarehouseBundleOperationResult.Ok(task.BundleId, task.TaskRef, WarehouseTaskStatus.InExecution);
    }

    public WarehouseBundleOperationResult Scan(
        long taskId,
        string barcode,
        string scanType,
        string? deviceId,
        string? operatorId = null)
    {
        var task = RequireTask(taskId);
        if (task.Status is WarehouseTaskStatus.Executed or WarehouseTaskStatus.Confirmed or WarehouseTaskStatus.Cancelled)
        {
            return Fail("TASK_ALREADY_COMPLETED", "Задание уже завершено.");
        }

        if (!string.Equals(task.Status, WarehouseTaskStatus.InExecution, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("TASK_NOT_IN_EXECUTION", "Сначала начните задание.");
        }

        var normalizedType = scanType.Trim().ToUpperInvariant();
        var code = barcode.Trim();
        var lines = _data.GetWarehouseTaskLines(taskId);
        if (lines.Count == 0)
        {
            return Fail("TASK_HAS_NO_LINES", "У задания нет строк.");
        }

        var line = lines[0];
        var now = DateTime.Now;

        if (normalizedType is "HU" or "SCAN_HU")
        {
            if (!string.Equals(line.ExpectedHuCode, code, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("HU_NOT_IN_TASK", "HU не из задания.");
            }

            if (string.Equals(line.Status, WarehouseTaskLineStatus.Done, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(line.ScannedHuCode))
            {
                return Fail("HU_ALREADY_SCANNED", "HU уже отсканирован.");
            }

            _data.UpdateWarehouseTaskLineScan(
                line.Id,
                WarehouseTaskLineStatus.Scanned,
                code,
                line.ScannedLocationId,
                now,
                deviceId,
                operatorId,
                null,
                null);

            _data.AddWarehouseTaskEvent(new WarehouseTaskEvent
            {
                TaskId = taskId,
                TaskLineId = line.Id,
                EventType = WarehouseTaskEventType.ScanHu,
                EventAt = now,
                DeviceId = deviceId,
                OperatorId = operatorId,
                HuCode = code
            });

            return WarehouseBundleOperationResult.Ok(task.BundleId, task.TaskRef, WarehouseTaskStatus.InExecution);
        }

        if (normalizedType is "LOCATION" or "SCAN_LOCATION")
        {
            var location = _data.GetLocations().FirstOrDefault(loc =>
                               string.Equals(loc.Code, code, StringComparison.OrdinalIgnoreCase))
                           ?? _data.FindLocationById(long.TryParse(code, out var id) ? id : -1);

            if (location == null)
            {
                return Fail("LOCATION_NOT_FOUND", "Место не найдено.");
            }

            if (line.ToLocationId.HasValue && line.ToLocationId.Value != location.Id)
            {
                return Fail("WRONG_LOCATION", "Место назначения неверное.");
            }

            if (string.IsNullOrWhiteSpace(line.ScannedHuCode) && string.IsNullOrWhiteSpace(line.ExpectedHuCode))
            {
                return Fail("SCAN_HU_FIRST", "Сначала отсканируйте HU.");
            }

            _data.UpdateWarehouseTaskLineScan(
                line.Id,
                WarehouseTaskLineStatus.Done,
                line.ScannedHuCode ?? line.ExpectedHuCode,
                location.Id,
                now,
                deviceId,
                operatorId,
                null,
                null);

            _data.AddWarehouseTaskEvent(new WarehouseTaskEvent
            {
                TaskId = taskId,
                TaskLineId = line.Id,
                EventType = WarehouseTaskEventType.ScanLocation,
                EventAt = now,
                DeviceId = deviceId,
                OperatorId = operatorId,
                LocationId = location.Id,
                Message = location.Code
            });

            return WarehouseBundleOperationResult.Ok(task.BundleId, task.TaskRef, WarehouseTaskStatus.InExecution);
        }

        return Fail("INVALID_SCAN_TYPE", "Неизвестный тип скана.");
    }

    public WarehouseBundleOperationResult CompleteTask(long taskId, string? deviceId, string? operatorId = null)
    {
        var task = RequireTask(taskId);
        if (task.Status is WarehouseTaskStatus.Executed or WarehouseTaskStatus.Confirmed)
        {
            return WarehouseBundleOperationResult.Ok(task.BundleId, task.TaskRef, task.Status);
        }

        var lines = _data.GetWarehouseTaskLines(taskId);
        if (lines.Any(line => line.Status is not WarehouseTaskLineStatus.Done and not WarehouseTaskLineStatus.Cancelled))
        {
            return Fail("TASK_LINES_INCOMPLETE", "Не все шаги сканирования выполнены.");
        }

        var now = DateTime.Now;
        _data.UpdateWarehouseTaskStatus(
            taskId,
            WarehouseTaskStatus.Executed,
            null,
            now,
            null,
            null,
            deviceId,
            operatorId);

        _data.AddWarehouseTaskEvent(new WarehouseTaskEvent
        {
            TaskId = taskId,
            EventType = WarehouseTaskEventType.CompleteTask,
            EventAt = now,
            DeviceId = deviceId,
            OperatorId = operatorId,
            Message = "TSD complete"
        });

        _bundles.TryAdvanceBundleToExecuted(task.BundleId);
        return WarehouseBundleOperationResult.Ok(task.BundleId, task.TaskRef, WarehouseTaskStatus.Executed);
    }

    private WarehouseTask RequireTask(long taskId) =>
        _data.GetWarehouseTask(taskId) ?? throw new InvalidOperationException("Задание не найден.");

    private static WarehouseBundleOperationResult Fail(string code, string message) =>
        WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue { Code = code, Message = message });
}
