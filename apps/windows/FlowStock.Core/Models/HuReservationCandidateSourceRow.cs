namespace FlowStock.Core.Models;

public sealed class HuReservationCandidateSourceRow
{
    public string Source { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public double Qty { get; init; }
    public long? SourceOrderId { get; init; }
    public string? SourceOrderRef { get; init; }
    public long? SourcePrdDocId { get; init; }
    public string? SourcePrdRef { get; init; }
    public bool ShipReady { get; init; }
    public long? ReservedByOrderId { get; init; }
    public string? ReservedByOrderRef { get; init; }
    public string Note { get; init; } = string.Empty;
}
