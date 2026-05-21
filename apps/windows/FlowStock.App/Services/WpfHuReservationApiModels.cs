using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class WpfHuReservationCandidatesRequest
{
    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<WpfHuReservationCandidatesLineRequest> Lines { get; init; } =
        Array.Empty<WpfHuReservationCandidatesLineRequest>();

    [JsonPropertyName("exclude_hu_codes")]
    public IReadOnlyList<string> ExcludeHuCodes { get; init; } = Array.Empty<string>();
}

public sealed class WpfHuReservationCandidatesLineRequest
{
    [JsonPropertyName("client_line_key")]
    public string ClientLineKey { get; init; } = string.Empty;

    [JsonPropertyName("order_line_id")]
    public long? OrderLineId { get; init; }

    [JsonPropertyName("item_id")]
    public long ItemId { get; init; }

    [JsonPropertyName("qty_ordered")]
    public double QtyOrdered { get; init; }
}

public sealed class WpfHuReservationCandidatesResult
{
    public IReadOnlyList<WpfHuReservationCandidatesLineResult> Lines { get; init; } =
        Array.Empty<WpfHuReservationCandidatesLineResult>();
}

public sealed class WpfHuReservationCandidatesLineResult
{
    public string ClientLineKey { get; init; } = string.Empty;
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
    public double AvailableQty { get; init; }
    public double AutoSelectedQty { get; init; }
    public IReadOnlyList<WpfHuReservationCandidateRow> Candidates { get; init; } =
        Array.Empty<WpfHuReservationCandidateRow>();
}

public sealed class WpfHuReservationCandidateRow
{
    public string HuCode { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public long? SourceOrderId { get; init; }
    public string? SourceOrderRef { get; init; }
    public long? SourcePrdDocId { get; init; }
    public string? SourcePrdRef { get; init; }
    public double Qty { get; init; }
    public bool ShipReady { get; init; }
    public bool AutoSelected { get; init; }
    public long? ReservedByOrderId { get; init; }
    public string? ReservedByOrderRef { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class WpfHuReservationApplyRequest
{
    [JsonPropertyName("lines")]
    public IReadOnlyList<WpfHuReservationApplyLineRequest> Lines { get; init; } =
        Array.Empty<WpfHuReservationApplyLineRequest>();
}

public sealed class WpfHuReservationApplyLineRequest
{
    [JsonPropertyName("order_line_id")]
    public long OrderLineId { get; init; }

    [JsonPropertyName("selected_hu_codes")]
    public IReadOnlyList<string> SelectedHuCodes { get; init; } = Array.Empty<string>();
}

public sealed class WpfHuReservationApplyResult
{
    public bool Ok { get; init; }
    public long OrderId { get; init; }
    public IReadOnlyList<WpfHuReservationApplyLineResult> AppliedLines { get; init; } =
        Array.Empty<WpfHuReservationApplyLineResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class WpfHuReservationApplyLineResult
{
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double OrderedQty { get; init; }
    public double ReservedQty { get; init; }
    public int SelectedHuCount { get; init; }
}

public sealed class WpfHuReservationApplyError
{
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}
