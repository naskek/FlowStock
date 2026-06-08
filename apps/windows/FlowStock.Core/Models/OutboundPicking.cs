namespace FlowStock.Core.Models;

public sealed class OutboundPickingOrderRow
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string PartnerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int ExpectedHuCount { get; init; }
    public int PickedHuCount { get; init; }
    public double OrderedQty { get; init; }
    public double ShippedQty { get; init; }
    public double RemainingQty { get; init; }
    public double ScannedQty { get; init; }
    public bool IsComplete => ExpectedHuCount > 0 && PickedHuCount >= ExpectedHuCount;
}

public sealed class OutboundPickingOrderDetails
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string PartnerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long? DraftOutboundDocId { get; init; }
    public string? DraftOutboundDocRef { get; init; }
    public int ExpectedHuCount { get; init; }
    public int PickedHuCount { get; init; }
    public double OrderedQty { get; init; }
    public double ShippedQty { get; init; }
    public double RemainingQty { get; init; }
    public double ScannedQty { get; init; }
    public bool IsComplete => ExpectedHuCount > 0 && PickedHuCount >= ExpectedHuCount;
    public IReadOnlyList<OutboundPickingHuRow> Hus { get; init; } = Array.Empty<OutboundPickingHuRow>();
}

public sealed class OutboundPickingHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public string Status { get; init; } = OutboundPickingHuStatus.Pending;
    public double Qty { get; init; }
    public string ItemSummary { get; init; } = string.Empty;
    public IReadOnlyList<OutboundPickingHuLine> Lines { get; init; } = Array.Empty<OutboundPickingHuLine>();
}

public sealed class OutboundPickingHuLine
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public long? OrderLineId { get; init; }
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public double Qty { get; init; }
}

public sealed class OutboundPickingScanResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public bool AlreadyPicked { get; init; }
    public bool OutboundClosed { get; init; }
    public string? ClosedOutboundDocRef { get; init; }
    public OutboundPickingOrderDetails? Order { get; init; }

    public static OutboundPickingScanResult Failure(string errorCode, string message)
        => new() { Success = false, ErrorCode = errorCode, Message = message };
}

public sealed class OutboundPickingCompleteResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public bool OutboundClosed { get; init; }
    public long? ClosedOutboundDocId { get; init; }
    public string? ClosedOutboundDocRef { get; init; }
    public OutboundPickingOrderDetails? Order { get; init; }

    public static OutboundPickingCompleteResult Failure(string errorCode, string message)
        => new() { Success = false, ErrorCode = errorCode, Message = message };
}

public static class OutboundPickingHuStatus
{
    public const string Pending = "PENDING";
    public const string Picked = "PICKED";
}
