namespace FlowStock.Core.Models;

public sealed class CloseDocResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public CloseDocTiming? Timing { get; init; }
}

public sealed class CloseDocTiming
{
    public long? ValidateBuildCheckMs { get; set; }
    public long? LedgerTransactionMs { get; set; }
    public long? CollectAffectedOrdersMs { get; set; }
    public long? RefreshStatusMs { get; set; }
    public long? RefreshReceiptPlansMs { get; set; }
}

