namespace FlowStock.Core.Models;

public sealed class OverShippedOrderDiagnosticItem
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double QtyOrdered { get; init; }
    public double ShippedByApiReadModel { get; init; }
    public double ShippedByClosedOutbound { get; init; }
    public double ShippedByLedger { get; init; }
    public double OverShippedQty { get; init; }
    public IReadOnlyList<OverShippedOutboundDocLine> OutboundDocs { get; init; } = Array.Empty<OverShippedOutboundDocLine>();
    public IReadOnlyList<OverShippedLedgerEntry> LedgerEntries { get; init; } = Array.Empty<OverShippedLedgerEntry>();
    public string Recommendation { get; init; } = string.Empty;
}

public sealed class OverShippedOutboundDocLine
{
    public long DocId { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? ClosedAt { get; init; }
    public long DocLineId { get; init; }
    public double Qty { get; init; }
    public string? FromHu { get; init; }
    public long? OrderLineId { get; init; }
}

public sealed class OverShippedLedgerEntry
{
    public long LedgerId { get; init; }
    public long DocId { get; init; }
    public long ItemId { get; init; }
    public string? HuCode { get; init; }
    public double QtyDelta { get; init; }
}
