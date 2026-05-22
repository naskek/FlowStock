namespace FlowStock.Core.Models;

public sealed class HuReservationCandidatesQuery
{
    public long OrderId { get; init; }
    public IReadOnlyList<HuReservationCandidatesLineQuery> Lines { get; init; } = Array.Empty<HuReservationCandidatesLineQuery>();
    public IReadOnlyList<string> ExcludeHuCodes { get; init; } = Array.Empty<string>();
}

public sealed class HuReservationCandidatesLineQuery
{
    public string ClientLineKey { get; init; } = string.Empty;
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
}

public sealed class HuReservationCandidatesResult
{
    public IReadOnlyList<HuReservationCandidatesLineResult> Lines { get; init; } = Array.Empty<HuReservationCandidatesLineResult>();
}

public sealed class HuReservationCandidatesLineResult
{
    public string ClientLineKey { get; init; } = string.Empty;
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
    public double AvailableQty { get; init; }
    public double AutoSelectedQty { get; init; }
    public IReadOnlyList<HuReservationCandidateResult> Candidates { get; init; } = Array.Empty<HuReservationCandidateResult>();
}

public sealed class HuReservationCandidateResult
{
    public string HuCode { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public long? SourceOrderId { get; init; }
    public string? SourceOrderRef { get; init; }
    public long? SourcePrdDocId { get; init; }
    public string? SourcePrdRef { get; init; }
    public double Qty { get; init; }
    public bool ShipReady { get; init; }
    public bool AutoSelected { get; set; }
    public long? ReservedByOrderId { get; init; }
    public string? ReservedByOrderRef { get; init; }
    public string Note { get; init; } = string.Empty;
}
