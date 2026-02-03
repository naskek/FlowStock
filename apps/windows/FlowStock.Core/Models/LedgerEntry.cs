namespace FlowStock.Core.Models;

public sealed class LedgerEntry
{
    public long Id { get; init; }
    public DateTime Timestamp { get; init; }
    public long DocId { get; init; }
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public double QtyDelta { get; init; }
    public string? HuCode { get; init; }
}

