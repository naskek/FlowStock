namespace FlowStock.Core.Models;

public sealed class MarkingNeedCreationResult
{
    public int CreatedTaskCount { get; init; }
    public double CreatedQty { get; init; }
    public string Message { get; init; } = string.Empty;
}
