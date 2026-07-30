namespace FlowStock.Core.Models;

public sealed class CloseDocResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GeneratedLedgerEntry> GeneratedLedgerEntries { get; init; } =
        Array.Empty<GeneratedLedgerEntry>();
    public CloseDocTiming? Timing { get; init; }
}

public sealed class GeneratedLedgerEntry
{
    public long DocLineId { get; init; }
    public long LedgerEntryId { get; init; }
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public double QtyDelta { get; init; }
    public string? HuCode { get; init; }
}

public sealed class CloseDocTiming
{
    public long? ValidateBuildCheckMs { get; set; }
    public long? LedgerTransactionMs { get; set; }
    public long? CollectAffectedOrdersMs { get; set; }
    public long? RefreshStatusMs { get; set; }
    public long? RefreshReceiptPlansMs { get; set; }
}

