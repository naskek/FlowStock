using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Moq;

namespace FlowStock.Server.Tests.WarehouseTasks.Infrastructure;

internal sealed class WarehouseTaskStoreHarness
{
    private readonly Mock<IDataStore> _store = new(MockBehavior.Strict);
    private readonly Dictionary<long, WarehouseActionBundle> _bundles = new();
    private readonly Dictionary<long, WarehouseActionLine> _lines = new();
    private readonly Dictionary<long, WarehouseTask> _tasks = new();
    private readonly Dictionary<long, WarehouseTaskLine> _taskLines = new();
    private readonly Dictionary<long, WarehouseTaskEvent> _events = new();
    private long _nextBundleId = 1;
    private long _nextLineId = 1;
    private long _nextTaskId = 1;
    private long _nextTaskLineId = 1;
    private long _nextEventId = 1;

    public WarehouseTaskStoreHarness()
    {
        ConfigureStore();
    }

    public IDataStore Store => _store.Object;

    public WarehouseActionBundle AddBundle(string? source = null, string? status = null, string? bundleRef = null)
    {
        var now = DateTime.Now;
        var bundle = new WarehouseActionBundle
        {
            Id = _nextBundleId++,
            BundleRef = bundleRef ?? $"BND-{now:yyyy}-{_nextBundleId:000000}",
            Source = source ?? WarehouseBundleSource.Wpf,
            Status = status ?? WarehouseBundleStatus.Draft,
            CreatedAt = now,
            CreatedBy = "test"
        };
        _bundles[bundle.Id] = bundle;
        return bundle;
    }

    public WarehouseActionLine AddLine(
        long bundleId,
        string actionType,
        string? huCode = null,
        string payloadJson = "{}")
    {
        var lineNo = _lines.Values.Count(line => line.BundleId == bundleId) + 1;
        var now = DateTime.Now;
        var line = new WarehouseActionLine
        {
            Id = _nextLineId++,
            BundleId = bundleId,
            LineNo = lineNo,
            ActionType = actionType,
            Status = WarehouseActionLineStatus.Pending,
            HuCode = huCode,
            PayloadJson = payloadJson,
            CreatedAt = now,
            UpdatedAt = now
        };
        _lines[line.Id] = line;
        return line;
    }

    private void ConfigureStore()
    {
        _store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(_store.Object));

        _store.Setup(store => store.GetWarehouseActionBundle(It.IsAny<long>()))
            .Returns<long>(id => _bundles.TryGetValue(id, out var bundle) ? CloneBundle(bundle) : null);

        _store.Setup(store => store.FindWarehouseBundleByRef(It.IsAny<string>()))
            .Returns<string>(bundleRef => _bundles.Values.FirstOrDefault(bundle =>
                string.Equals(bundle.BundleRef, bundleRef, StringComparison.OrdinalIgnoreCase)) is { } found
                ? CloneBundle(found)
                : null);

        _store.Setup(store => store.GetWarehouseActionBundles(It.IsAny<string?>()))
            .Returns<string?>(status => _bundles.Values
                .Where(bundle => string.IsNullOrWhiteSpace(status)
                                 || string.Equals(bundle.Status, status, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(bundle => bundle.CreatedAt)
                .Select(CloneBundle)
                .ToArray());

        _store.Setup(store => store.GetMaxWarehouseBundleRefSequenceByYear(It.IsAny<int>()))
            .Returns<int>(year => _bundles.Values
                .Select(bundle => bundle.BundleRef)
                .Select(refValue => ParseSequence(refValue, year))
                .DefaultIfEmpty(0)
                .Max());

        _store.Setup(store => store.AddWarehouseActionBundle(It.IsAny<WarehouseActionBundle>()))
            .Returns<WarehouseActionBundle>(bundle =>
            {
                var id = _nextBundleId++;
                var stored = CloneBundle(bundle);
                stored = CopyBundle(stored, id: id);
                _bundles[id] = stored;
                return id;
            });

        _store.Setup(store => store.UpdateWarehouseActionBundleStatus(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Callback<long, string, DateTime?, string?, DateTime?, DateTime?, DateTime?, string?, string?, string?>(
                (bundleId, status, approvedAt, approvedBy, executedAt, completedAt, rejectedAt, rejectedBy, errorCode, errorMessage) =>
                {
                    if (!_bundles.TryGetValue(bundleId, out var bundle))
                    {
                        return;
                    }

                    _bundles[bundleId] = CopyBundle(
                        bundle,
                        status: status,
                        approvedAt: approvedAt ?? bundle.ApprovedAt,
                        approvedBy: approvedBy ?? bundle.ApprovedBy,
                        executedAt: executedAt ?? bundle.ExecutedAt,
                        completedAt: completedAt ?? bundle.CompletedAt,
                        rejectedAt: rejectedAt ?? bundle.RejectedAt,
                        rejectedBy: rejectedBy ?? bundle.RejectedBy,
                        errorCode: errorCode ?? bundle.ErrorCode,
                        errorMessage: errorMessage ?? bundle.ErrorMessage);
                });

        _store.Setup(store => store.GetWarehouseActionLine(It.IsAny<long>()))
            .Returns<long>(id => _lines.TryGetValue(id, out var line) ? CloneLine(line) : null);

        _store.Setup(store => store.GetWarehouseActionLines(It.IsAny<long>()))
            .Returns<long>(bundleId => _lines.Values
                .Where(line => line.BundleId == bundleId)
                .OrderBy(line => line.LineNo)
                .Select(CloneLine)
                .ToArray());

        _store.Setup(store => store.GetNextWarehouseActionLineNo(It.IsAny<long>()))
            .Returns<long>(bundleId => _lines.Values.Where(line => line.BundleId == bundleId).Select(line => line.LineNo).DefaultIfEmpty(0).Max() + 1);

        _store.Setup(store => store.AddWarehouseActionLine(It.IsAny<WarehouseActionLine>()))
            .Returns<WarehouseActionLine>(line =>
            {
                var id = _nextLineId++;
                _lines[id] = CopyLine(CloneLine(line), id: id);
                return id;
            });

        _store.Setup(store => store.UpdateWarehouseActionLine(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>()))
            .Callback<long, string, long?, string?, string?, string?, DateTime>(
                (lineId, status, targetDocId, resultJson, errorCode, errorMessage, updatedAt) =>
                {
                    if (!_lines.TryGetValue(lineId, out var line))
                    {
                        return;
                    }

                    _lines[lineId] = CopyLine(
                        line,
                        status: status,
                        targetDocId: targetDocId ?? line.TargetDocId,
                        resultJson: resultJson ?? line.ResultJson,
                        errorCode: errorCode,
                        errorMessage: errorMessage,
                        updatedAt: updatedAt);
                });

        _store.Setup(store => store.GetWarehouseTask(It.IsAny<long>()))
            .Returns<long>(id => _tasks.TryGetValue(id, out var task) ? CloneTask(task) : null);

        _store.Setup(store => store.FindWarehouseTaskByRef(It.IsAny<string>()))
            .Returns<string>(taskRef => _tasks.Values.FirstOrDefault(task =>
                string.Equals(task.TaskRef, taskRef, StringComparison.OrdinalIgnoreCase)) is { } found
                ? CloneTask(found)
                : null);

        _store.Setup(store => store.GetWarehouseTasksByBundle(It.IsAny<long>()))
            .Returns<long>(bundleId => _tasks.Values.Where(task => task.BundleId == bundleId).Select(CloneTask).ToArray());

        _store.Setup(store => store.GetActiveWarehouseTasks(It.IsAny<string?>()))
            .Returns<string?>(_ => _tasks.Values.Select(CloneTask).ToArray());

        _store.Setup(store => store.GetMaxWarehouseTaskRefSequenceByYear(It.IsAny<int>()))
            .Returns<int>(year => _tasks.Values.Select(task => task.TaskRef).Select(refValue => ParseSequence(refValue, year)).DefaultIfEmpty(0).Max());

        _store.Setup(store => store.AddWarehouseTask(It.IsAny<WarehouseTask>()))
            .Returns<WarehouseTask>(task =>
            {
                var id = _nextTaskId++;
                _tasks[id] = CopyTask(CloneTask(task), id: id);
                return id;
            });

        _store.Setup(store => store.UpdateWarehouseTaskStatus(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Callback<long, string, DateTime?, DateTime?, DateTime?, DateTime?, string?, string?>(
                (taskId, status, startedAt, executedAt, confirmedAt, cancelledAt, deviceId, user) =>
                {
                    if (!_tasks.TryGetValue(taskId, out var task))
                    {
                        return;
                    }

                    _tasks[taskId] = CopyTask(
                        task,
                        status: status,
                        startedAt: startedAt ?? task.StartedAt,
                        executedAt: executedAt ?? task.ExecutedAt,
                        confirmedAt: confirmedAt ?? task.ConfirmedAt,
                        cancelledAt: cancelledAt ?? task.CancelledAt,
                        assignedToDeviceId: deviceId ?? task.AssignedToDeviceId,
                        assignedToUser: user ?? task.AssignedToUser);
                });

        _store.Setup(store => store.GetWarehouseTaskLine(It.IsAny<long>()))
            .Returns<long>(id => _taskLines.TryGetValue(id, out var line) ? CloneTaskLine(line) : null);

        _store.Setup(store => store.GetWarehouseTaskLines(It.IsAny<long>()))
            .Returns<long>(taskId => _taskLines.Values.Where(line => line.TaskId == taskId).Select(CloneTaskLine).ToArray());

        _store.Setup(store => store.AddWarehouseTaskLine(It.IsAny<WarehouseTaskLine>()))
            .Returns<WarehouseTaskLine>(line =>
            {
                var id = _nextTaskLineId++;
                _taskLines[id] = CopyTaskLine(CloneTaskLine(line), id: id);
                return id;
            });

        _store.Setup(store => store.UpdateWarehouseTaskLineScan(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Callback<long, string, string?, long?, DateTime?, string?, string?, string?, string?>(
                (lineId, status, scannedHu, scannedLocationId, scannedAt, deviceId, operatorId, errorCode, errorMessage) =>
                {
                    if (!_taskLines.TryGetValue(lineId, out var line))
                    {
                        return;
                    }

                    _taskLines[lineId] = CopyTaskLine(
                        line,
                        status: status,
                        scannedHuCode: scannedHu ?? line.ScannedHuCode,
                        scannedLocationId: scannedLocationId ?? line.ScannedLocationId,
                        scannedAt: scannedAt ?? line.ScannedAt,
                        deviceId: deviceId ?? line.DeviceId,
                        operatorId: operatorId ?? line.OperatorId,
                        errorCode: errorCode,
                        errorMessage: errorMessage);
                });

        _store.Setup(store => store.AddWarehouseTaskEvent(It.IsAny<WarehouseTaskEvent>()))
            .Returns<WarehouseTaskEvent>(warehouseEvent =>
            {
                var id = _nextEventId++;
                _events[id] = CopyEvent(warehouseEvent, id: id);
                return id;
            });

        _store.Setup(store => store.GetWarehouseTaskEvents(It.IsAny<long>()))
            .Returns<long>(taskId => _events.Values.Where(e => e.TaskId == taskId).OrderBy(e => e.EventAt).ToArray());

        _store.Setup(store => store.IsHuLockedByActiveWarehouseTask(It.IsAny<string>(), It.IsAny<long?>()))
            .Returns<string, long?>((huCode, excludeBundleId) =>
            {
                var normalized = huCode.Trim();
                return _bundles.Values.Any(bundle =>
                           WarehouseBundleStatus.IsActiveForHuLock(bundle.Status)
                           && bundle.Id != excludeBundleId
                           && _lines.Values.Any(line =>
                               line.BundleId == bundle.Id
                               && string.Equals(line.HuCode, normalized, StringComparison.OrdinalIgnoreCase)))
                       || _tasks.Values.Any(task =>
                           task.BundleId != excludeBundleId
                           && _bundles.TryGetValue(task.BundleId, out var bundle)
                           && WarehouseBundleStatus.IsActiveForHuLock(bundle.Status)
                           && _taskLines.Values.Any(line =>
                               line.TaskId == task.Id
                               && string.Equals(line.ExpectedHuCode, normalized, StringComparison.OrdinalIgnoreCase)));
            });
    }

    private static int ParseSequence(string refValue, int year)
    {
        var parts = refValue.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[1], year.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(parts[^1], out var sequence) ? sequence : 0;
    }

    private static WarehouseActionBundle CloneBundle(WarehouseActionBundle bundle) => CopyBundle(bundle);

    private static WarehouseActionLine CloneLine(WarehouseActionLine line) => CopyLine(line);

    private static WarehouseTask CloneTask(WarehouseTask task) => CopyTask(task);

    private static WarehouseTaskLine CloneTaskLine(WarehouseTaskLine line) => CopyTaskLine(line);

    private static WarehouseTaskEvent CopyEvent(WarehouseTaskEvent source, long? id = null) =>
        new()
        {
            Id = id ?? source.Id,
            TaskId = source.TaskId,
            TaskLineId = source.TaskLineId,
            EventType = source.EventType,
            EventAt = source.EventAt,
            DeviceId = source.DeviceId,
            OperatorId = source.OperatorId,
            HuCode = source.HuCode,
            LocationId = source.LocationId,
            PayloadJson = source.PayloadJson,
            Message = source.Message
        };

    private static WarehouseActionBundle CopyBundle(
        WarehouseActionBundle source,
        long? id = null,
        string? status = null,
        DateTime? approvedAt = null,
        string? approvedBy = null,
        DateTime? executedAt = null,
        DateTime? completedAt = null,
        DateTime? rejectedAt = null,
        string? rejectedBy = null,
        string? errorCode = null,
        string? errorMessage = null) =>
        new()
        {
            Id = id ?? source.Id,
            BundleRef = source.BundleRef,
            Source = source.Source,
            Status = status ?? source.Status,
            CreatedAt = source.CreatedAt,
            CreatedBy = source.CreatedBy,
            ApprovedAt = approvedAt ?? source.ApprovedAt,
            ApprovedBy = approvedBy ?? source.ApprovedBy,
            ExecutedAt = executedAt ?? source.ExecutedAt,
            CompletedAt = completedAt ?? source.CompletedAt,
            RejectedAt = rejectedAt ?? source.RejectedAt,
            RejectedBy = rejectedBy ?? source.RejectedBy,
            Comment = source.Comment,
            ErrorCode = errorCode ?? source.ErrorCode,
            ErrorMessage = errorMessage ?? source.ErrorMessage
        };

    private static WarehouseActionLine CopyLine(
        WarehouseActionLine source,
        long? id = null,
        string? status = null,
        long? targetDocId = null,
        string? resultJson = null,
        string? errorCode = null,
        string? errorMessage = null,
        DateTime? updatedAt = null) =>
        new()
        {
            Id = id ?? source.Id,
            BundleId = source.BundleId,
            LineNo = source.LineNo,
            ActionType = source.ActionType,
            Status = status ?? source.Status,
            SourceOrderId = source.SourceOrderId,
            TargetOrderId = source.TargetOrderId,
            SourceDocId = source.SourceDocId,
            TargetDocId = targetDocId ?? source.TargetDocId,
            ItemId = source.ItemId,
            HuCode = source.HuCode,
            FromLocationId = source.FromLocationId,
            ToLocationId = source.ToLocationId,
            Qty = source.Qty,
            PayloadJson = source.PayloadJson,
            ResultJson = resultJson ?? source.ResultJson,
            ErrorCode = errorCode ?? source.ErrorCode,
            ErrorMessage = errorMessage ?? source.ErrorMessage,
            CreatedAt = source.CreatedAt,
            UpdatedAt = updatedAt ?? source.UpdatedAt
        };

    private static WarehouseTask CopyTask(
        WarehouseTask source,
        long? id = null,
        string? status = null,
        DateTime? startedAt = null,
        DateTime? executedAt = null,
        DateTime? confirmedAt = null,
        DateTime? cancelledAt = null,
        string? assignedToDeviceId = null,
        string? assignedToUser = null) =>
        new()
        {
            Id = id ?? source.Id,
            TaskRef = source.TaskRef,
            BundleId = source.BundleId,
            ActionLineId = source.ActionLineId,
            TaskType = source.TaskType,
            Status = status ?? source.Status,
            AssignedToDeviceId = assignedToDeviceId ?? source.AssignedToDeviceId,
            AssignedToUser = assignedToUser ?? source.AssignedToUser,
            CreatedAt = source.CreatedAt,
            StartedAt = startedAt ?? source.StartedAt,
            ExecutedAt = executedAt ?? source.ExecutedAt,
            ConfirmedAt = confirmedAt ?? source.ConfirmedAt,
            CancelledAt = cancelledAt ?? source.CancelledAt,
            Comment = source.Comment
        };

    private static WarehouseTaskLine CopyTaskLine(
        WarehouseTaskLine source,
        long? id = null,
        string? status = null,
        string? scannedHuCode = null,
        long? scannedLocationId = null,
        DateTime? scannedAt = null,
        string? deviceId = null,
        string? operatorId = null,
        string? errorCode = null,
        string? errorMessage = null) =>
        new()
        {
            Id = id ?? source.Id,
            TaskId = source.TaskId,
            LineNo = source.LineNo,
            ExpectedHuCode = source.ExpectedHuCode,
            ExpectedItemId = source.ExpectedItemId,
            ExpectedQty = source.ExpectedQty,
            FromLocationId = source.FromLocationId,
            ToLocationId = source.ToLocationId,
            OrderId = source.OrderId,
            DocId = source.DocId,
            Status = status ?? source.Status,
            ScannedHuCode = scannedHuCode ?? source.ScannedHuCode,
            ScannedLocationId = scannedLocationId ?? source.ScannedLocationId,
            ScannedAt = scannedAt ?? source.ScannedAt,
            DeviceId = deviceId ?? source.DeviceId,
            OperatorId = operatorId ?? source.OperatorId,
            ErrorCode = errorCode ?? source.ErrorCode,
            ErrorMessage = errorMessage ?? source.ErrorMessage
        };
}
