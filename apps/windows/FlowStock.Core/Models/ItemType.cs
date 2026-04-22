namespace FlowStock.Core.Models;

public sealed class ItemType
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Code { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsVisibleInProductCatalog { get; init; }
    public bool EnableMinStockControl { get; init; }
}
