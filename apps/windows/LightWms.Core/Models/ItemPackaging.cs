namespace LightWms.Core.Models;

public sealed class ItemPackaging
{
    public long Id { get; init; }
    public long ItemId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double FactorToBase { get; init; }
    public bool IsActive { get; init; } = true;
    public int SortOrder { get; init; }
}
