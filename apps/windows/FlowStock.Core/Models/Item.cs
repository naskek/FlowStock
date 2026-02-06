namespace FlowStock.Core.Models;

public sealed class Item
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public string BaseUom { get; init; } = "èâ";
    public long? DefaultPackagingId { get; init; }
    public string? Brand { get; init; }
    public string? Volume { get; init; }
    public int? ShelfLifeMonths { get; init; }
    public long? TaraId { get; init; }
    public string? TaraName { get; init; }
}
