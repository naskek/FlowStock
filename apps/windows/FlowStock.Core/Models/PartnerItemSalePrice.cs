namespace FlowStock.Core.Models;

public sealed class PartnerItemSalePrice
{
    public long Id { get; init; }
    public long PartnerId { get; init; }
    public string PartnerName { get; init; } = string.Empty;
    public string? PartnerCode { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public decimal UnitPriceGross { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record PartnerItemSalePricePage(
    IReadOnlyList<PartnerItemSalePrice> Items,
    int TotalCount,
    int Limit,
    int Offset);
