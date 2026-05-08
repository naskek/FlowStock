namespace FlowStock.Core.Models;

public sealed class MarkingNeedCreationResult
{
    public int CreatedTaskCount { get; init; }
    public double CreatedQty { get; init; }
    public IReadOnlyList<string> DebugSummary { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = string.Empty;
}
