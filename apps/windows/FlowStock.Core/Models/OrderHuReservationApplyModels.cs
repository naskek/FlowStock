namespace FlowStock.Core.Models;

public sealed class OrderHuReservationApplyRequest
{
    public IReadOnlyList<OrderHuReservationApplyLineRequest> Lines { get; init; } =
        Array.Empty<OrderHuReservationApplyLineRequest>();
}

public sealed class OrderHuReservationApplyLineRequest
{
    public long OrderLineId { get; init; }
    public IReadOnlyList<string> SelectedHuCodes { get; init; } = Array.Empty<string>();
}

public sealed class OrderHuReservationApplyResult
{
    public bool Ok { get; init; } = true;
    public long OrderId { get; init; }
    public IReadOnlyList<OrderHuReservationApplyLineResult> AppliedLines { get; init; } =
        Array.Empty<OrderHuReservationApplyLineResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class OrderHuReservationApplyLineResult
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double OrderedQty { get; init; }
    public double ReservedQty { get; init; }
    public int SelectedHuCount { get; init; }
    public IReadOnlyList<OrderHuReservationAppliedHuResult> SelectedHu { get; init; } =
        Array.Empty<OrderHuReservationAppliedHuResult>();
}

public sealed class OrderHuReservationAppliedHuResult
{
    public string HuCode { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public double Qty { get; init; }
    public bool ShipReady { get; init; }
}
