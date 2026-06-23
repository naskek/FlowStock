using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class WpfHuBindingManageItemRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int HuCount { get; init; }
}

public sealed class WpfHuBindingManageHuFilter
{
    public string? HuSearch { get; init; }
    public string? OrderSearch { get; init; }
    public string? PartnerSearch { get; init; }
    public string State { get; init; } = "ALL";
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

public sealed class WpfHuBindingManageHuPage
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Limit { get; init; }
    public int Offset { get; init; }
    public IReadOnlyList<WpfHuBindingManageHuRow> HuRows { get; init; } =
        Array.Empty<WpfHuBindingManageHuRow>();
}

public sealed class WpfHuBindingManageHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string LocationDisplay { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public bool IsMixed { get; init; }
    public long? OriginInternalOrderId { get; init; }
    public string? OriginInternalOrderRef { get; init; }
    public DateTime? FirstReceiptAt { get; init; }
    public WpfHuBindingManageHuAssignment? CurrentAssignment { get; init; }
}

public sealed class WpfHuBindingManageHuAssignment
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public long OrderLineId { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public double ReservedQty { get; init; }
}

public sealed class WpfHuBindingManageTargetLine
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public DateTime? DueAt { get; init; }
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
    public double QtyShipped { get; init; }
    public IReadOnlyList<string> CurrentBoundHuCodes { get; init; } = Array.Empty<string>();
    public double CurrentBoundQty { get; init; }
    public double MaxAdditionalBindQty { get; init; }
}

public sealed class WpfHuBindingManageApplyRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "replace_final_selection";

    [JsonPropertyName("expected_hu_states")]
    public IReadOnlyList<WpfHuBindingManageExpectedHuState> ExpectedHuStates { get; init; } =
        Array.Empty<WpfHuBindingManageExpectedHuState>();

    [JsonPropertyName("lines")]
    public IReadOnlyList<WpfHuBindingManageApplyLineRequest> Lines { get; init; } =
        Array.Empty<WpfHuBindingManageApplyLineRequest>();
}

public sealed class WpfHuBindingManageExpectedHuState
{
    [JsonPropertyName("hu_code")]
    public string HuCode { get; init; } = string.Empty;

    [JsonPropertyName("item_id")]
    public long ItemId { get; init; }

    [JsonPropertyName("expected_qty")]
    public double ExpectedQty { get; init; }

    [JsonPropertyName("expected_order_id")]
    public long? ExpectedOrderId { get; init; }

    [JsonPropertyName("expected_order_line_id")]
    public long? ExpectedOrderLineId { get; init; }
}

public sealed class WpfHuBindingManageApplyLineRequest
{
    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("order_line_id")]
    public long OrderLineId { get; init; }

    [JsonPropertyName("expected_bound_hu_codes")]
    public IReadOnlyList<string> ExpectedBoundHuCodes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("final_hu_codes")]
    public IReadOnlyList<string> FinalHuCodes { get; init; } = Array.Empty<string>();
}

public sealed class WpfHuBindingManageApplyResult
{
    public bool Ok { get; init; }
    public IReadOnlyList<WpfHuBindingManageApplyOrderResult> Orders { get; init; } =
        Array.Empty<WpfHuBindingManageApplyOrderResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class WpfHuBindingManageApplyOrderResult
{
    public long OrderId { get; init; }
    public IReadOnlyList<WpfHuBindingApplyFinalLineResult> AppliedLines { get; init; } =
        Array.Empty<WpfHuBindingApplyFinalLineResult>();
}

public sealed class WpfHuBindingManageApplyError
{
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}
