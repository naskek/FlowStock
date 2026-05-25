namespace FlowStock.Core.Commercial;

public sealed class PricingQuoteRequest
{
    public long ItemId { get; init; }
    public long PartnerId { get; init; }
    public double Qty { get; init; }
    public DateOnly AsOfDate { get; init; }
    public decimal ManualDiscountPercent { get; init; }
    public long? PriceGroupOverrideId { get; init; }
}

public sealed class PricingQuoteResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorCode { get; init; }
    public long? PriceGroupId { get; init; }
    public decimal BasePrice { get; init; }
    public decimal VolumeDiscountPercent { get; init; }
    public decimal PartnerDiscountPercent { get; init; }
    public decimal ManualDiscountPercent { get; init; }
    public decimal FinalDiscountPercent { get; init; }
    public decimal FinalPrice { get; init; }
    public decimal LineTotal { get; init; }
    public string Currency { get; init; } = "RUB";
    public decimal CatalogBasePrice { get; init; }
    public decimal GroupPrice { get; init; }
    public string PriceSource { get; init; } = PriceSourceKindMapper.ToCode(PriceSourceKind.Base);

    public static PricingQuoteResult Failure(string errorCode) => new()
    {
        IsSuccess = false,
        ErrorCode = errorCode
    };
}
