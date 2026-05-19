namespace FlowStock.Core.Services.Warehouse;

public sealed class WarehouseBundleLineInput
{
    public string ActionType { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public long? SourceOrderId { get; init; }
    public long? TargetOrderId { get; init; }
    public long? ItemId { get; init; }
    public string? HuCode { get; init; }
    public long? FromLocationId { get; init; }
    public long? ToLocationId { get; init; }
    public double? Qty { get; init; }
}

public sealed class WarehouseBundleIssue
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? LineNo { get; init; }
}

public sealed class WarehouseBundlePreviewResult
{
    public bool Valid => Errors.Count == 0;
    public IReadOnlyList<WarehouseBundleIssue> Errors { get; init; } = Array.Empty<WarehouseBundleIssue>();
    public IReadOnlyList<WarehouseBundleIssue> Warnings { get; init; } = Array.Empty<WarehouseBundleIssue>();
    public IReadOnlyList<WarehouseBundleLinePreview> Lines { get; init; } = Array.Empty<WarehouseBundleLinePreview>();
}

public sealed class WarehouseBundleLinePreview
{
    public int LineNo { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}

public sealed class WarehouseBundleOperationResult
{
    public bool Success { get; init; }
    public long? BundleId { get; init; }
    public string? BundleRef { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<WarehouseBundleIssue> Errors { get; init; } = Array.Empty<WarehouseBundleIssue>();
    public IReadOnlyList<WarehouseBundleIssue> Warnings { get; init; } = Array.Empty<WarehouseBundleIssue>();

    public static WarehouseBundleOperationResult Ok(long bundleId, string bundleRef, string status, string? message = null) =>
        new()
        {
            Success = true,
            BundleId = bundleId,
            BundleRef = bundleRef,
            Status = status,
            Message = message
        };

    public static WarehouseBundleOperationResult Fail(params WarehouseBundleIssue[] errors) =>
        new() { Success = false, Errors = errors };

    public static WarehouseBundleOperationResult Fail(IEnumerable<WarehouseBundleIssue> errors) =>
        new() { Success = false, Errors = errors.ToArray() };
}

public sealed class WarehouseMoveHuPayload
{
    public string? HuCode { get; init; }
    public long? ItemId { get; init; }
    public double? Qty { get; init; }
    public long? FromLocationId { get; init; }
    public long? ToLocationId { get; init; }
}

public sealed class WarehouseAdoptPalletPlanPayload
{
    public long? SourceInternalOrderId { get; init; }
    public long? TargetCustomerOrderId { get; init; }
}
