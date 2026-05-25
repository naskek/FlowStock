namespace FlowStock.Core.Commercial;

public sealed class PriceGroup
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Currency { get; init; } = "RUB";
    public VatMode VatMode { get; init; } = VatMode.Included;
    public bool IsDefault { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; } = true;
    public decimal DefaultDiscountPercent { get; init; }
    public decimal DefaultMarkupPercent { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class PartnerCommercialSettings
{
    public long PartnerId { get; init; }
    public long? PriceGroupId { get; init; }
    public decimal DefaultDiscountPercent { get; init; }
    public string? PaymentTerms { get; init; }
    public string? DeliveryTerms { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class ItemPrice
{
    public long Id { get; init; }
    public long ItemId { get; init; }
    public long PriceGroupId { get; init; }
    public decimal Price { get; init; }
    public string Currency { get; init; } = "RUB";
    public decimal? VatRate { get; init; }
    public bool? VatIncluded { get; init; }
    public string? UomCode { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public bool IsActive { get; init; } = true;
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class ItemPriceCatalogRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public string? ItemTypeName { get; init; }
    public long? ItemPriceId { get; init; }
    public long? BaseItemPriceId { get; init; }
    public long PriceGroupId { get; init; }
    public decimal? Price { get; init; }
    public decimal? BasePrice { get; init; }
    public decimal? GroupOverridePrice { get; init; }
    public decimal? CalculatedPrice { get; init; }
    public decimal? GroupDiscountPercent { get; init; }
    public decimal? GroupMarkupPercent { get; init; }
    public string? PriceSource { get; init; }
    public string? PriceMissingReason { get; init; }
    public string? Currency { get; init; }
    public string? BaseCurrency { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public DateOnly? BaseValidFrom { get; init; }
    public DateOnly? BaseValidTo { get; init; }
    public bool? IsActive { get; init; }
    public bool? BaseIsActive { get; init; }
    public string? Comment { get; init; }
    public string? BaseComment { get; init; }

    public bool HasBasePrice => BasePrice is > 0;
    public bool HasPrice => CalculatedPrice is > 0 || Price is > 0;
}

public sealed class ItemPricingOverviewRow
{
    public long PriceGroupId { get; init; }
    public string PriceGroupName { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
    public decimal DefaultDiscountPercent { get; init; }
    public decimal DefaultMarkupPercent { get; init; }
    public decimal? BasePrice { get; init; }
    public decimal? OverridePrice { get; init; }
    public decimal? CalculatedPrice { get; init; }
    public string PriceSource { get; init; } = PriceSourceKindMapper.ToCode(PriceSourceKind.Base);
    public string? Currency { get; init; }
    public long? ItemPriceId { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public bool? IsActive { get; init; }
    public string? Comment { get; init; }
}

public sealed record UpsertItemPriceCommand
{
    public long ItemId { get; init; }
    public long PriceGroupId { get; init; }
    public decimal Price { get; init; }
    public string Currency { get; init; } = "RUB";
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public bool IsActive { get; init; } = true;
    public string? Comment { get; init; }
    public decimal? VatRate { get; init; }
    public bool? VatIncluded { get; init; }
    public string? UomCode { get; init; }
}

public sealed class VolumeDiscountRule
{
    public long Id { get; init; }
    public VolumeDiscountScope ScopeType { get; init; }
    public long? PriceGroupId { get; init; }
    public long? PartnerId { get; init; }
    public long? ItemTypeId { get; init; }
    public long? ItemId { get; init; }
    public double MinQty { get; init; }
    public decimal DiscountPercent { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public bool IsActive { get; init; } = true;
    public string? Comment { get; init; }
}
