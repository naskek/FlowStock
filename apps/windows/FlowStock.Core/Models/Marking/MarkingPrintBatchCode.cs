namespace FlowStock.Core.Models.Marking;

public sealed class MarkingPrintBatchCode
{
    public Guid Id { get; init; }
    public Guid PrintBatchId { get; init; }
    public Guid MarkingCodeId { get; init; }
    public int SequenceNo { get; init; }
    public DateTime CreatedAt { get; init; }
}
