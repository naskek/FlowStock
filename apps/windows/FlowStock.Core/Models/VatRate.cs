namespace FlowStock.Core.Models;

public sealed class VatRate
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal Rate { get; init; }
    public bool IsActive { get; init; } = true;
    public int SortOrder { get; init; }
}
