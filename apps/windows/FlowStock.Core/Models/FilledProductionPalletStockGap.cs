namespace FlowStock.Core.Models;

public sealed class FilledProductionPalletStockGap
{
    public long PalletId { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long? ToLocationId { get; init; }
    public double PlannedQty { get; init; }
    public double LedgerQty { get; init; }
    public double MissingQty { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Filled;
    public DateTime? FilledAt { get; init; }
}

public sealed class ProtectedFilledProductionPallet
{
    public string HuCode { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
}

public sealed class HuBalanceCorrectionCandidateBalance
{
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public bool Protected { get; init; }
}
