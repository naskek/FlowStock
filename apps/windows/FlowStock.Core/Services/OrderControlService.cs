using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderControlService
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _store;

    public OrderControlService(IDataStore store)
    {
        _store = store;
    }

    public OrderControlPreviewResult Preview(IReadOnlyList<long> orderIds)
    {
        var normalizedOrderIds = NormalizeOrderIds(orderIds);
        if (normalizedOrderIds.Count == 0)
        {
            return new OrderControlPreviewResult
            {
                CanCreate = false,
                ErrorCode = OrderControlErrorCodes.OrderNotEligible,
                Message = "Выберите хотя бы один заказ."
            };
        }

        var snapshot = BuildSnapshot(_store, normalizedOrderIds, currentTaskId: null);
        return new OrderControlPreviewResult
        {
            CanCreate = snapshot.CanCreate,
            Orders = snapshot.Orders,
            Hus = snapshot.Hus,
            Warnings = snapshot.Warnings,
            ErrorCode = snapshot.ErrorCode,
            Message = snapshot.Message
        };
    }

    public OrderControlCreateResult Create(IReadOnlyList<long> orderIds, string? createdBy, string? comment)
    {
        OrderControlCreateResult? result = null;
        _store.ExecuteInTransaction(store =>
        {
            var normalizedOrderIds = NormalizeOrderIds(orderIds);
            if (!store.LockOrdersForUpdate(normalizedOrderIds))
            {
                result = OrderControlCreateResult.Failure(
                    OrderControlErrorCodes.OrderNotEligible,
                    "Один или несколько выбранных заказов не найдены.");
                return;
            }

            var snapshot = BuildSnapshot(store, normalizedOrderIds, currentTaskId: null);
            if (!snapshot.CanCreate)
            {
                result = OrderControlCreateResult.Failure(
                    snapshot.ErrorCode ?? OrderControlErrorCodes.OrderNotEligible,
                    snapshot.Message ?? "Контроль нельзя создать.");
                return;
            }

            var now = DateTime.Now;
            var year = now.Year;
            var sequence = store.GetMaxOrderControlTaskRefSequenceByYear(year) + 1;
            var taskRef = $"CTRL-{year}-{sequence:000000}";
            var task = new OrderControlTask
            {
                TaskRef = taskRef,
                Status = OrderControlTaskStatus.New,
                CreatedAt = now,
                CreatedBy = NormalizeOptional(createdBy),
                ExpectedHuCount = snapshot.Hus.Count,
                CheckedHuCount = 0,
                DiscrepancyHuCount = 0,
                SnapshotHash = snapshot.SnapshotHash,
                Comment = NormalizeOptional(comment)
            };

            var taskId = store.AddOrderControlTask(task);
            foreach (var order in snapshot.Orders)
            {
                store.AddOrderControlTaskOrder(new OrderControlTaskOrder
                {
                    TaskId = taskId,
                    OrderId = order.OrderId,
                    OrderRef = order.OrderRef,
                    PartnerName = order.PartnerName,
                    IsActive = true
                });
            }

            foreach (var hu in snapshot.Hus)
            {
                var taskHuId = store.AddOrderControlTaskHu(new OrderControlTaskHu
                {
                    TaskId = taskId,
                    HuCode = hu.HuCode,
                    NormalizedHu = NormalizeHu(hu.HuCode)!,
                    Status = OrderControlHuStatus.Pending,
                    Qty = hu.Qty,
                    ItemSummary = hu.ItemSummary,
                    SnapshotHash = BuildHuSnapshotHash(hu.Lines)
                });

                foreach (var line in hu.Lines)
                {
                    store.AddOrderControlTaskHuLine(new OrderControlTaskHuLine
                    {
                        TaskId = taskId,
                        TaskHuId = taskHuId,
                        HuCode = hu.HuCode,
                        OrderId = line.OrderId,
                        OrderRef = line.OrderRef,
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        ItemName = line.ItemName,
                        Qty = line.Qty,
                        LocationId = line.LocationId,
                        LocationCode = line.LocationCode,
                        SourceType = line.SourceType
                    });
                }
            }

            store.AddOrderControlEvent(new OrderControlEvent
            {
                TaskId = taskId,
                EventType = OrderControlEventType.Created,
                EventAt = now,
                OperatorId = NormalizeOptional(createdBy),
                PayloadJson = JsonSerializer.Serialize(new { order_ids = normalizedOrderIds, hu_count = snapshot.Hus.Count }),
                Message = "Задание контроля создано."
            });

            result = new OrderControlCreateResult
            {
                Success = true,
                Message = "Задание контроля создано.",
                Task = LoadDetails(store, taskId)
            };
        });

        return result ?? OrderControlCreateResult.Failure("CREATE_FAILED", "Не удалось создать задание контроля.");
    }

    public IReadOnlyList<OrderControlTaskSummary> GetTasks(string? status, bool activeOnly)
        => _store.GetOrderControlTasks(NormalizeOptional(status), activeOnly);

    public OrderControlTaskDetails? GetDetails(long taskId)
        => LoadDetails(_store, taskId);

    public OrderControlTaskDetails? Start(long taskId, string? deviceId, string? operatorId)
    {
        OrderControlTaskDetails? details = null;
        _store.ExecuteInTransaction(store =>
        {
            if (!store.LockOrderControlTask(taskId))
            {
                return;
            }

            var task = store.GetOrderControlTask(taskId);
            if (task == null || string.Equals(task.Status, OrderControlTaskStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.New, StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.Now;
                store.UpdateOrderControlTaskStatus(
                    taskId,
                    OrderControlTaskStatus.InExecution,
                    now,
                    null,
                    null,
                    null,
                    NormalizeOptional(deviceId),
                    null,
                    null);
                store.AddOrderControlEvent(new OrderControlEvent
                {
                    TaskId = taskId,
                    EventType = OrderControlEventType.Started,
                    EventAt = now,
                    DeviceId = NormalizeOptional(deviceId),
                    OperatorId = NormalizeOptional(operatorId),
                    Message = "Задание взято в работу."
                });
            }

            details = LoadDetails(store, taskId);
        });

        return details;
    }

    public OrderControlScanResult Scan(long taskId, string? huCode, string? requestId, string? deviceId, string? operatorId)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return OrderControlScanResult.Failure(OrderControlErrorCodes.HuNotInTask, "Отсканируйте HU.");
        }

        requestId = NormalizeOptional(requestId) ?? Guid.NewGuid().ToString("N");
        OrderControlScanResult? result = null;
        _store.ExecuteInTransaction(store =>
        {
            if (!store.LockOrderControlTask(taskId))
            {
                result = OrderControlScanResult.Failure(OrderControlErrorCodes.TaskNotFound, "Задание не найдено.");
                return;
            }

            var existingEvent = store.FindOrderControlEventByRequestId(taskId, requestId);
            if (existingEvent != null)
            {
                if (!string.Equals(NormalizeHu(existingEvent.HuCode), normalizedHu, StringComparison.OrdinalIgnoreCase))
                {
                    result = OrderControlScanResult.Failure(
                        OrderControlErrorCodes.IdempotencyConflict,
                        "request_id уже использован для другой HU.",
                        LoadDetails(store, taskId));
                    return;
                }

                result = BuildIdempotentScanRetryResult(store, taskId, existingEvent);
                return;
            }

            var task = store.GetOrderControlTask(taskId);
            if (task == null)
            {
                result = OrderControlScanResult.Failure(OrderControlErrorCodes.TaskNotFound, "Задание не найдено.");
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                result = RejectScan(store, taskId, null, normalizedHu, requestId, deviceId, operatorId,
                    OrderControlErrorCodes.TaskCancelled, "Задание отменено.");
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.Completed, StringComparison.OrdinalIgnoreCase))
            {
                result = RejectScan(store, taskId, null, normalizedHu, requestId, deviceId, operatorId,
                    OrderControlErrorCodes.TaskIncomplete, "Задание уже завершено.");
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.New, StringComparison.OrdinalIgnoreCase))
            {
                store.UpdateOrderControlTaskStatus(
                    taskId,
                    OrderControlTaskStatus.InExecution,
                    DateTime.Now,
                    null,
                    null,
                    null,
                    NormalizeOptional(deviceId),
                    null,
                    null);
            }

            var taskHu = store.GetOrderControlTaskHuByNormalizedHu(taskId, normalizedHu);
            if (taskHu == null)
            {
                result = RejectScan(store, taskId, null, normalizedHu, requestId, deviceId, operatorId,
                    OrderControlErrorCodes.HuNotInTask, "HU не входит в задание контроля.");
                return;
            }

            if (string.Equals(taskHu.Status, OrderControlHuStatus.Checked, StringComparison.OrdinalIgnoreCase))
            {
                store.AddOrderControlEvent(new OrderControlEvent
                {
                    TaskId = taskId,
                    TaskHuId = taskHu.Id,
                    EventType = OrderControlEventType.ScanDuplicate,
                    EventAt = DateTime.Now,
                    DeviceId = NormalizeOptional(deviceId),
                    OperatorId = NormalizeOptional(operatorId),
                    HuCode = normalizedHu,
                    RequestId = requestId,
                    Message = "HU уже проверена."
                });
                result = new OrderControlScanResult
                {
                    Success = true,
                    AlreadyChecked = true,
                    Message = "HU уже проверена.",
                    Task = LoadDetails(store, taskId)
                };
                return;
            }

            var discrepancy = DetectBlockingDiscrepancy(store, taskId, taskHu);
            if (discrepancy != null)
            {
                store.UpdateOrderControlTaskHuStatus(
                    taskHu.Id,
                    OrderControlHuStatus.Discrepancy,
                    null,
                    NormalizeOptional(deviceId),
                    NormalizeOptional(operatorId),
                    discrepancy.Value.Code,
                    discrepancy.Value.Message);
                store.UpdateOrderControlTaskProgress(taskId);
                result = RejectScan(store, taskId, taskHu.Id, normalizedHu, requestId, deviceId, operatorId,
                    discrepancy.Value.Code, discrepancy.Value.Message);
                return;
            }

            var now = DateTime.Now;
            store.UpdateOrderControlTaskHuStatus(
                taskHu.Id,
                OrderControlHuStatus.Checked,
                now,
                NormalizeOptional(deviceId),
                NormalizeOptional(operatorId),
                null,
                null);
            store.UpdateOrderControlTaskProgress(taskId);
            store.AddOrderControlEvent(new OrderControlEvent
            {
                TaskId = taskId,
                TaskHuId = taskHu.Id,
                EventType = OrderControlEventType.ScanAccepted,
                EventAt = now,
                DeviceId = NormalizeOptional(deviceId),
                OperatorId = NormalizeOptional(operatorId),
                HuCode = normalizedHu,
                RequestId = requestId,
                Message = "HU проверена."
            });
            result = new OrderControlScanResult
            {
                Success = true,
                Message = "HU проверена.",
                Task = LoadDetails(store, taskId)
            };
        });

        return result ?? OrderControlScanResult.Failure("SCAN_FAILED", "Не удалось обработать скан.");
    }

    public OrderControlCompleteResult Complete(long taskId, string? deviceId, string? operatorId)
    {
        OrderControlCompleteResult? result = null;
        _store.ExecuteInTransaction(store =>
        {
            if (!store.LockOrderControlTask(taskId))
            {
                result = OrderControlCompleteResult.Failure(OrderControlErrorCodes.TaskNotFound, "Задание не найдено.");
                return;
            }

            var task = store.GetOrderControlTask(taskId);
            if (task == null)
            {
                result = OrderControlCompleteResult.Failure(OrderControlErrorCodes.TaskNotFound, "Задание не найдено.");
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                result = OrderControlCompleteResult.Failure(OrderControlErrorCodes.TaskCancelled, "Задание отменено.", LoadDetails(store, taskId));
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.Completed, StringComparison.OrdinalIgnoreCase))
            {
                result = new OrderControlCompleteResult
                {
                    Success = true,
                    Message = "Задание контроля уже завершено.",
                    Task = LoadDetails(store, taskId)
                };
                return;
            }

            store.UpdateOrderControlTaskProgress(taskId);
            task = store.GetOrderControlTask(taskId)!;
            if (task.DiscrepancyHuCount > 0)
            {
                result = OrderControlCompleteResult.Failure(
                    OrderControlErrorCodes.ExpectedSetChanged,
                    "В задании есть blocking discrepancy. Отмените задание и создайте новое.",
                    LoadDetails(store, taskId));
                return;
            }

            if (task.ExpectedHuCount <= 0 || task.CheckedHuCount < task.ExpectedHuCount)
            {
                result = OrderControlCompleteResult.Failure(
                    OrderControlErrorCodes.TaskIncomplete,
                    $"Проверено {task.CheckedHuCount} из {task.ExpectedHuCount} HU.",
                    LoadDetails(store, taskId));
                return;
            }

            var snapshotValidation = ValidateCurrentSnapshotUnchanged(store, task);
            if (snapshotValidation != null)
            {
                result = OrderControlCompleteResult.Failure(
                    snapshotValidation.Value.Code,
                    snapshotValidation.Value.Message,
                    LoadDetails(store, taskId));
                return;
            }

            var now = DateTime.Now;
            store.UpdateOrderControlTaskStatus(
                taskId,
                OrderControlTaskStatus.Completed,
                null,
                now,
                null,
                null,
                NormalizeOptional(deviceId),
                null,
                null);
            store.DeactivateOrderControlTaskOrders(taskId);
            store.AddOrderControlEvent(new OrderControlEvent
            {
                TaskId = taskId,
                EventType = OrderControlEventType.Completed,
                EventAt = now,
                DeviceId = NormalizeOptional(deviceId),
                OperatorId = NormalizeOptional(operatorId),
                Message = "Задание контроля завершено."
            });
            result = new OrderControlCompleteResult
            {
                Success = true,
                Message = "Задание контроля завершено.",
                Task = LoadDetails(store, taskId)
            };
        });

        return result ?? OrderControlCompleteResult.Failure("COMPLETE_FAILED", "Не удалось завершить задание.");
    }

    public OrderControlTaskDetails? Cancel(long taskId, string? cancelledBy)
    {
        OrderControlTaskDetails? details = null;
        _store.ExecuteInTransaction(store =>
        {
            if (!store.LockOrderControlTask(taskId))
            {
                return;
            }

            var task = store.GetOrderControlTask(taskId);
            if (task == null)
            {
                return;
            }

            if (string.Equals(task.Status, OrderControlTaskStatus.Completed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Status, OrderControlTaskStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                details = LoadDetails(store, taskId);
                return;
            }

            var now = DateTime.Now;
            store.UpdateOrderControlTaskStatus(
                taskId,
                OrderControlTaskStatus.Cancelled,
                null,
                null,
                now,
                NormalizeOptional(cancelledBy),
                null,
                null,
                null);
            foreach (var hu in store.GetOrderControlTaskHus(taskId)
                         .Where(hu => string.Equals(hu.Status, OrderControlHuStatus.Pending, StringComparison.OrdinalIgnoreCase)))
            {
                store.UpdateOrderControlTaskHuStatus(
                    hu.Id,
                    OrderControlHuStatus.Cancelled,
                    null,
                    null,
                    null,
                    null,
                    null);
            }

            store.UpdateOrderControlTaskProgress(taskId);
            store.DeactivateOrderControlTaskOrders(taskId);
            store.AddOrderControlEvent(new OrderControlEvent
            {
                TaskId = taskId,
                EventType = OrderControlEventType.Cancelled,
                EventAt = now,
                OperatorId = NormalizeOptional(cancelledBy),
                Message = "Задание контроля отменено."
            });
            details = LoadDetails(store, taskId);
        });

        return details;
    }

    private static OrderControlScanResult BuildIdempotentScanRetryResult(
        IDataStore store,
        long taskId,
        OrderControlEvent existingEvent)
    {
        if (string.Equals(existingEvent.EventType, OrderControlEventType.ScanAccepted, StringComparison.OrdinalIgnoreCase))
        {
            return new OrderControlScanResult
            {
                Success = true,
                Message = existingEvent.Message ?? "HU проверена.",
                Task = LoadDetails(store, taskId)
            };
        }

        if (string.Equals(existingEvent.EventType, OrderControlEventType.ScanDuplicate, StringComparison.OrdinalIgnoreCase))
        {
            return new OrderControlScanResult
            {
                Success = true,
                AlreadyChecked = true,
                Message = existingEvent.Message ?? "HU уже проверена.",
                Task = LoadDetails(store, taskId)
            };
        }

        if (string.Equals(existingEvent.EventType, OrderControlEventType.ScanRejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existingEvent.EventType, OrderControlEventType.Discrepancy, StringComparison.OrdinalIgnoreCase))
        {
            return OrderControlScanResult.Failure(
                existingEvent.ErrorCode ?? "SCAN_FAILED",
                existingEvent.Message ?? "Скан отклонен.",
                LoadDetails(store, taskId));
        }

        return OrderControlScanResult.Failure(
            OrderControlErrorCodes.IdempotencyConflict,
            "request_id уже использован другим событием.",
            LoadDetails(store, taskId));
    }

    private static OrderControlScanResult RejectScan(
        IDataStore store,
        long taskId,
        long? taskHuId,
        string huCode,
        string requestId,
        string? deviceId,
        string? operatorId,
        string errorCode,
        string message)
    {
        store.AddOrderControlEvent(new OrderControlEvent
        {
            TaskId = taskId,
            TaskHuId = taskHuId,
            EventType = errorCode == OrderControlErrorCodes.ExpectedSetChanged
                || errorCode == OrderControlErrorCodes.HuNoPhysicalStock
                || errorCode == OrderControlErrorCodes.HuAlreadyShipped
                    ? OrderControlEventType.Discrepancy
                    : OrderControlEventType.ScanRejected,
            EventAt = DateTime.Now,
            DeviceId = NormalizeOptional(deviceId),
            OperatorId = NormalizeOptional(operatorId),
            HuCode = huCode,
            RequestId = requestId,
            ErrorCode = errorCode,
            Message = message
        });

        return OrderControlScanResult.Failure(errorCode, message, LoadDetails(store, taskId));
    }

    private static (string Code, string Message)? DetectBlockingDiscrepancy(
        IDataStore store,
        long taskId,
        OrderControlTaskHu taskHu)
    {
        var orders = store.GetOrderControlTaskOrders(taskId);
        var currentSnapshot = BuildSnapshot(store, orders.Select(order => order.OrderId).ToArray(), taskId);
        if (!currentSnapshot.CanCreate
            && !string.Equals(currentSnapshot.ErrorCode, OrderControlErrorCodes.NoExpectedHu, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeSnapshotError(currentSnapshot);
        }

        var currentHu = currentSnapshot.Hus.FirstOrDefault(hu =>
            string.Equals(NormalizeHu(hu.HuCode), taskHu.NormalizedHu, StringComparison.OrdinalIgnoreCase));
        if (currentHu == null)
        {
            if (store.IsOutboundHuShipped(taskHu.NormalizedHu))
            {
                return (OrderControlErrorCodes.HuAlreadyShipped, "HU уже отгружена или не доступна к контролю.");
            }

            return (OrderControlErrorCodes.HuNoPhysicalStock, "HU не имеет актуального физического остатка ledger.");
        }

        var currentHash = BuildHuSnapshotHash(currentHu.Lines);
        if (!string.Equals(currentHash, taskHu.SnapshotHash, StringComparison.OrdinalIgnoreCase))
        {
            return (OrderControlErrorCodes.ExpectedSetChanged, "Ожидаемый состав HU изменился после создания задания.");
        }

        return null;
    }

    private static (string Code, string Message)? ValidateCurrentSnapshotUnchanged(IDataStore store, OrderControlTask task)
    {
        var orders = store.GetOrderControlTaskOrders(task.Id);
        var currentSnapshot = BuildSnapshot(store, orders.Select(order => order.OrderId).ToArray(), task.Id);
        if (!currentSnapshot.CanCreate)
        {
            return NormalizeSnapshotError(currentSnapshot);
        }

        if (!string.Equals(currentSnapshot.SnapshotHash, task.SnapshotHash, StringComparison.OrdinalIgnoreCase))
        {
            return (OrderControlErrorCodes.ExpectedSetChanged, "Ожидаемый набор HU изменился после создания задания.");
        }

        return null;
    }

    private static (string Code, string Message) NormalizeSnapshotError(Snapshot snapshot)
    {
        var code = snapshot.ErrorCode ?? OrderControlErrorCodes.ExpectedSetChanged;
        if (string.Equals(code, OrderControlErrorCodes.NoExpectedHu, StringComparison.OrdinalIgnoreCase))
        {
            return (OrderControlErrorCodes.ExpectedSetChanged, "Ожидаемый набор HU изменился после создания задания.");
        }

        return (code, snapshot.Message ?? "Текущее состояние заказа не позволяет продолжить контроль.");
    }

    private static OrderControlTaskDetails? LoadDetails(IDataStore store, long taskId)
    {
        var task = store.GetOrderControlTask(taskId);
        if (task == null)
        {
            return null;
        }

        return new OrderControlTaskDetails
        {
            Task = task,
            Orders = store.GetOrderControlTaskOrders(taskId),
            Hus = store.GetOrderControlTaskHus(taskId),
            HuLines = store.GetOrderControlTaskHuLines(taskId),
            Events = store.GetOrderControlEvents(taskId)
        };
    }

    private static Snapshot BuildSnapshot(IDataStore store, IReadOnlyList<long> orderIds, long? currentTaskId)
    {
        var orderResults = new List<OrderControlPreviewOrder>();
        var lines = new List<OrderControlTaskHuLine>();
        foreach (var orderId in orderIds)
        {
            var order = store.GetOrder(orderId);
            if (order == null)
            {
                orderResults.Add(new OrderControlPreviewOrder
                {
                    OrderId = orderId,
                    IsEligible = false,
                    ErrorCode = OrderControlErrorCodes.OrderNotEligible,
                    Message = "Заказ не найден."
                });
                continue;
            }

            var orderError = currentTaskId.HasValue
                ? ValidateOrderForCurrentTask(store, order, currentTaskId.Value)
                : ValidateOrderForCreate(store, order);
            orderResults.Add(new OrderControlPreviewOrder
            {
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                PartnerName = order.PartnerName,
                IsEligible = orderError == null,
                ErrorCode = orderError?.Code,
                Message = orderError?.Message
            });

            if (orderError != null)
            {
                continue;
            }

            lines.AddRange(CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(store, order.Id)
                .Where(line => line.Qty > QtyTolerance && !string.IsNullOrWhiteSpace(NormalizeHu(line.HuCode)))
                .Select(line => new OrderControlTaskHuLine
                {
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    HuCode = NormalizeHu(line.HuCode) ?? line.HuCode,
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    Qty = line.Qty,
                    LocationId = line.FromLocationId,
                    LocationCode = line.FromLocationCode,
                    SourceType = line.SourceType
                }));
        }

        var hus = lines
            .GroupBy(line => NormalizeHu(line.HuCode) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var huLines = group.OrderBy(line => line.OrderId)
                    .ThenBy(line => line.OrderLineId)
                    .ThenBy(line => line.ItemId)
                    .ToArray();
                return new OrderControlPreviewHu
                {
                    HuCode = group.Key,
                    OrderRefs = string.Join(", ", huLines.Select(line => line.OrderRef).Distinct(StringComparer.OrdinalIgnoreCase)),
                    ItemSummary = string.Join(", ", huLines.Select(line => string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : line.ItemName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                    LocationCode = string.Join(", ", huLines.Select(line => line.LocationCode).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)),
                    SourceType = string.Join("+", huLines.Select(line => line.SourceType).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Qty = huLines.Sum(line => Math.Max(0, line.Qty)),
                    Lines = huLines
                };
            })
            .OrderBy(hu => hu.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var invalid = orderResults.FirstOrDefault(order => !order.IsEligible);
        if (invalid != null)
        {
            return new Snapshot(
                false,
                orderResults,
                hus,
                Array.Empty<string>(),
                invalid.ErrorCode ?? OrderControlErrorCodes.OrderNotEligible,
                invalid.Message ?? "Есть недопустимые заказы.",
                BuildSnapshotHash(hus));
        }

        if (hus.Length == 0)
        {
            return new Snapshot(
                false,
                orderResults,
                hus,
                Array.Empty<string>(),
                OrderControlErrorCodes.NoExpectedHu,
                "Для выбранных заказов нет ожидаемых HU.",
                string.Empty);
        }

        return new Snapshot(true, orderResults, hus, Array.Empty<string>(), null, null, BuildSnapshotHash(hus));
    }

    private static (string Code, string Message)? ValidateOrderForCreate(IDataStore store, Order order)
        => ValidateOrder(store, order, currentTaskId: null);

    private static (string Code, string Message)? ValidateOrderForCurrentTask(IDataStore store, Order order, long currentTaskId)
        => ValidateOrder(store, order, currentTaskId);

    private static (string Code, string Message)? ValidateOrder(IDataStore store, Order order, long? currentTaskId)
    {
        if (order.Type != OrderType.Customer || order.Status != OrderStatus.Accepted)
        {
            return (OrderControlErrorCodes.OrderNotEligible, "Контроль доступен только для клиентских заказов в статусе Готов.");
        }

        var active = store.FindActiveOrderControlForOrder(order.Id);
        if (active != null && (!currentTaskId.HasValue || active.Task.Id != currentTaskId.Value))
        {
            return (OrderControlErrorCodes.ActiveControlExists, $"Заказ уже в активном контроле {active.Task.TaskRef}.");
        }

        if (store.HasStartedOutboundForOrder(order.Id))
        {
            return (OrderControlErrorCodes.OutboundInProgress, "По заказу уже начата отгрузка.");
        }

        return null;
    }

    private static IReadOnlyList<long> NormalizeOrderIds(IReadOnlyList<long> orderIds)
        => orderIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    private static string? NormalizeHu(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildSnapshotHash(IReadOnlyList<OrderControlPreviewHu> hus)
        => Hash(string.Join("|", hus.Select(hu => $"{hu.HuCode}:{BuildHuSnapshotHash(hu.Lines)}")));

    private static string BuildHuSnapshotHash(IReadOnlyList<OrderControlTaskHuLine> lines)
        => Hash(string.Join("|", lines
            .OrderBy(line => line.OrderId)
            .ThenBy(line => line.OrderLineId)
            .ThenBy(line => line.ItemId)
            .ThenBy(line => line.LocationId ?? 0)
            .Select(line => string.Join(
                ":",
                line.OrderId,
                line.OrderLineId,
                line.ItemId,
                line.LocationId?.ToString() ?? "",
                line.SourceType,
                line.Qty.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Snapshot(
        bool CanCreate,
        IReadOnlyList<OrderControlPreviewOrder> Orders,
        IReadOnlyList<OrderControlPreviewHu> Hus,
        IReadOnlyList<string> Warnings,
        string? ErrorCode,
        string? Message,
        string SnapshotHash);
}
