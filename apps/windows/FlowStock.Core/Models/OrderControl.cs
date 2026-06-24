namespace FlowStock.Core.Models;

public static class OrderControlTaskStatus
{
    public const string New = "NEW";
    public const string InExecution = "IN_EXECUTION";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static bool IsActive(string? status)
        => string.Equals(status, New, StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, InExecution, StringComparison.OrdinalIgnoreCase);
}

public static class OrderControlHuStatus
{
    public const string Pending = "PENDING";
    public const string Checked = "CHECKED";
    public const string Discrepancy = "DISCREPANCY";
    public const string Cancelled = "CANCELLED";
}

public static class OrderControlEventType
{
    public const string Created = "CREATED";
    public const string Started = "STARTED";
    public const string ScanAccepted = "SCAN_ACCEPTED";
    public const string ScanDuplicate = "SCAN_DUPLICATE";
    public const string ScanRejected = "SCAN_REJECTED";
    public const string Discrepancy = "DISCREPANCY";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
}

public static class OrderControlErrorCodes
{
    public const string OrderNotEligible = "ORDER_NOT_ELIGIBLE";
    public const string NoExpectedHu = "NO_EXPECTED_HU";
    public const string ActiveControlExists = "ACTIVE_CONTROL_EXISTS";
    public const string OutboundInProgress = "OUTBOUND_IN_PROGRESS";
    public const string ExpectedSetChanged = "EXPECTED_SET_CHANGED";
    public const string HuNotInTask = "HU_NOT_IN_TASK";
    public const string HuAlreadyChecked = "HU_ALREADY_CHECKED";
    public const string HuNoPhysicalStock = "HU_NO_PHYSICAL_STOCK";
    public const string HuAlreadyShipped = "HU_ALREADY_SHIPPED";
    public const string TaskIncomplete = "TASK_INCOMPLETE";
    public const string TaskCancelled = "TASK_CANCELLED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string TaskNotFound = "TASK_NOT_FOUND";
}

public sealed class OrderControlTask
{
    public long Id { get; init; }
    public string TaskRef { get; init; } = string.Empty;
    public string Status { get; init; } = OrderControlTaskStatus.New;
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancelledBy { get; init; }
    public string? AssignedToDeviceId { get; init; }
    public int ExpectedHuCount { get; init; }
    public int CheckedHuCount { get; init; }
    public int DiscrepancyHuCount { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public string? Comment { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class OrderControlTaskOrder
{
    public long Id { get; init; }
    public long TaskId { get; init; }
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class OrderControlTaskHu
{
    public long Id { get; init; }
    public long TaskId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public string NormalizedHu { get; init; } = string.Empty;
    public string Status { get; init; } = OrderControlHuStatus.Pending;
    public double Qty { get; init; }
    public string ItemSummary { get; init; } = string.Empty;
    public string SnapshotHash { get; init; } = string.Empty;
    public DateTime? CheckedAt { get; init; }
    public string? CheckedByDeviceId { get; init; }
    public string? CheckedByOperator { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class OrderControlTaskHuLine
{
    public long Id { get; init; }
    public long TaskHuId { get; init; }
    public long TaskId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public long? LocationId { get; init; }
    public string? LocationCode { get; init; }
    public string SourceType { get; init; } = "OUTBOUND_READY";
}

public sealed class OrderControlEvent
{
    public long Id { get; init; }
    public long TaskId { get; init; }
    public long? TaskHuId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public DateTime EventAt { get; init; }
    public string? DeviceId { get; init; }
    public string? OperatorId { get; init; }
    public string? HuCode { get; init; }
    public string? RequestId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

public sealed class OrderControlTaskSummary
{
    public OrderControlTask Task { get; init; } = new();
    public IReadOnlyList<OrderControlTaskOrder> Orders { get; init; } = Array.Empty<OrderControlTaskOrder>();
}

public sealed class OrderControlTaskDetails
{
    public OrderControlTask Task { get; init; } = new();
    public IReadOnlyList<OrderControlTaskOrder> Orders { get; init; } = Array.Empty<OrderControlTaskOrder>();
    public IReadOnlyList<OrderControlTaskHu> Hus { get; init; } = Array.Empty<OrderControlTaskHu>();
    public IReadOnlyList<OrderControlTaskHuLine> HuLines { get; init; } = Array.Empty<OrderControlTaskHuLine>();
    public IReadOnlyList<OrderControlEvent> Events { get; init; } = Array.Empty<OrderControlEvent>();
}

public sealed class OrderControlPreviewOrder
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public bool IsEligible { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

public sealed class OrderControlPreviewHu
{
    public string HuCode { get; init; } = string.Empty;
    public string OrderRefs { get; init; } = string.Empty;
    public string ItemSummary { get; init; } = string.Empty;
    public string? LocationCode { get; init; }
    public string SourceType { get; init; } = "OUTBOUND_READY";
    public double Qty { get; init; }
    public IReadOnlyList<OrderControlTaskHuLine> Lines { get; init; } = Array.Empty<OrderControlTaskHuLine>();
}

public sealed class OrderControlPreviewResult
{
    public bool CanCreate { get; init; }
    public IReadOnlyList<OrderControlPreviewOrder> Orders { get; init; } = Array.Empty<OrderControlPreviewOrder>();
    public IReadOnlyList<OrderControlPreviewHu> Hus { get; init; } = Array.Empty<OrderControlPreviewHu>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

public sealed class OrderControlCreateResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public OrderControlTaskDetails? Task { get; init; }

    public static OrderControlCreateResult Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, Message = message };
}

public sealed class OrderControlScanResult
{
    public bool Success { get; init; }
    public bool AlreadyChecked { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public OrderControlTaskDetails? Task { get; init; }

    public static OrderControlScanResult Failure(string code, string message, OrderControlTaskDetails? task = null)
        => new() { Success = false, ErrorCode = code, Message = message, Task = task };
}

public sealed class OrderControlCompleteResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public OrderControlTaskDetails? Task { get; init; }

    public static OrderControlCompleteResult Failure(string code, string message, OrderControlTaskDetails? task = null)
        => new() { Success = false, ErrorCode = code, Message = message, Task = task };
}
