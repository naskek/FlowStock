namespace FlowStock.Core.Models.Marking;

public sealed class MarkingPrintBatch
{
    public Guid Id { get; init; }
    public Guid MarkingOrderId { get; init; }
    public int BatchNumber { get; init; }
    public string Status { get; init; } = MarkingPrintBatchStatus.New;
    public int CodesCount { get; init; }
    public string? PrinterTargetType { get; init; }
    public string? PrinterTargetValue { get; init; }
    public bool DebugLayout { get; init; }
    public Guid? ReprintOfBatchId { get; init; }
    public DateTime? PrintedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? Notes { get; init; }
}
