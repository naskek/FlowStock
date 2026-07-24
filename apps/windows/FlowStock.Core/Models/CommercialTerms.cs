namespace FlowStock.Core.Models;

public enum SalePriceSource
{
    None,
    PartnerItem,
    ItemDefault,
    Manual
}

public sealed record CommercialTermsResolution(
    decimal UnitPriceGross,
    decimal VatRate,
    SalePriceSource PriceSource);

public sealed record CommercialTermsPreview(
    long PartnerId,
    long ItemId,
    decimal? AutomaticUnitPriceGross,
    SalePriceSource PriceSource,
    decimal? VatRate,
    string? IssueCode,
    string? IssueMessage);
