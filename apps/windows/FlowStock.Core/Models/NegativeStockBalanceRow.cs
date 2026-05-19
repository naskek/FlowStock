namespace FlowStock.Core.Models;

public sealed class NegativeStockBalanceRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public string? HuCode { get; init; }
    public double Qty { get; init; }
    public long? LastLedgerEntryId { get; init; }
    public long? LastDocId { get; init; }
    public string? LastDocRef { get; init; }
    public DocType? LastDocType { get; init; }
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public DateTime? LastMovementAt { get; init; }
}
