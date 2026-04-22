namespace FlowStock.Core.Models;

public sealed class WriteOffReason
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}
