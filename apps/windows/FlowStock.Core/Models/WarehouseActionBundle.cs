namespace FlowStock.Core.Models;

public sealed class WarehouseActionBundle
{
    public long Id { get; init; }
    public string BundleRef { get; init; } = string.Empty;
    public string Source { get; init; } = WarehouseBundleSource.Wpf;
    public string Status { get; init; } = WarehouseBundleStatus.Draft;
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ExecutedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? RejectedAt { get; init; }
    public string? RejectedBy { get; init; }
    public string? Comment { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class WarehouseActionLine
{
    public long Id { get; init; }
    public long BundleId { get; init; }
    public int LineNo { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string Status { get; init; } = WarehouseActionLineStatus.Pending;
    public long? SourceOrderId { get; init; }
    public long? TargetOrderId { get; init; }
    public long? SourceDocId { get; init; }
    public long? TargetDocId { get; init; }
    public long? ItemId { get; init; }
    public string? HuCode { get; init; }
    public long? FromLocationId { get; init; }
    public long? ToLocationId { get; init; }
    public double? Qty { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public string? ResultJson { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class WarehouseTask
{
    public long Id { get; init; }
    public string TaskRef { get; init; } = string.Empty;
    public long BundleId { get; init; }
    public long ActionLineId { get; init; }
    public string TaskType { get; init; } = string.Empty;
    public string Status { get; init; } = WarehouseTaskStatus.New;
    public string? AssignedToDeviceId { get; init; }
    public string? AssignedToUser { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? ExecutedAt { get; init; }
    public DateTime? ConfirmedAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? Comment { get; init; }
}

public sealed class WarehouseTaskLine
{
    public long Id { get; init; }
    public long TaskId { get; init; }
    public int LineNo { get; init; }
    public string? ExpectedHuCode { get; init; }
    public long? ExpectedItemId { get; init; }
    public double? ExpectedQty { get; init; }
    public long? FromLocationId { get; init; }
    public long? ToLocationId { get; init; }
    public long? OrderId { get; init; }
    public long? DocId { get; init; }
    public string Status { get; init; } = WarehouseTaskLineStatus.Pending;
    public string? ScannedHuCode { get; init; }
    public long? ScannedLocationId { get; init; }
    public DateTime? ScannedAt { get; init; }
    public string? DeviceId { get; init; }
    public string? OperatorId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class WarehouseTaskEvent
{
    public long Id { get; init; }
    public long TaskId { get; init; }
    public long? TaskLineId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public DateTime EventAt { get; init; }
    public string? DeviceId { get; init; }
    public string? OperatorId { get; init; }
    public string? HuCode { get; init; }
    public long? LocationId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public string? Message { get; init; }
}
