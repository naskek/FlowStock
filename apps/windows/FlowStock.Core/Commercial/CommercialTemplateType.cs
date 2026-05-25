namespace FlowStock.Core.Commercial;

public enum CommercialTemplateType
{
    CommercialOffer,
    PriceTag,
    PriceList
}

public static class CommercialTemplateTypeMapper
{
    public static string ToCode(CommercialTemplateType type) => type switch
    {
        CommercialTemplateType.CommercialOffer => "COMMERCIAL_OFFER",
        CommercialTemplateType.PriceTag => "PRICE_TAG",
        CommercialTemplateType.PriceList => "PRICE_LIST",
        _ => "COMMERCIAL_OFFER"
    };

    public static CommercialTemplateType? FromCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "COMMERCIAL_OFFER" => CommercialTemplateType.CommercialOffer,
        "PRICE_TAG" => CommercialTemplateType.PriceTag,
        "PRICE_LIST" => CommercialTemplateType.PriceList,
        _ => null
    };
}
