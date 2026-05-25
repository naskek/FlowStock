namespace FlowStock.Core.Commercial;

public enum VolumeDiscountScope
{
    PartnerItem,
    Partner,
    Item,
    ItemType,
    PriceGroup,
    Global
}

public static class VolumeDiscountScopeMapper
{
    public static string ToCode(VolumeDiscountScope scope) => scope switch
    {
        VolumeDiscountScope.PartnerItem => "PARTNER_ITEM",
        VolumeDiscountScope.Partner => "PARTNER",
        VolumeDiscountScope.Item => "ITEM",
        VolumeDiscountScope.ItemType => "ITEM_TYPE",
        VolumeDiscountScope.PriceGroup => "PRICE_GROUP",
        VolumeDiscountScope.Global => "GLOBAL",
        _ => "GLOBAL"
    };

    public static VolumeDiscountScope? FromCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "PARTNER_ITEM" => VolumeDiscountScope.PartnerItem,
        "PARTNER" => VolumeDiscountScope.Partner,
        "ITEM" => VolumeDiscountScope.Item,
        "ITEM_TYPE" => VolumeDiscountScope.ItemType,
        "PRICE_GROUP" => VolumeDiscountScope.PriceGroup,
        "GLOBAL" => VolumeDiscountScope.Global,
        _ => null
    };

    public static int Priority(VolumeDiscountScope scope) => scope switch
    {
        VolumeDiscountScope.PartnerItem => 0,
        VolumeDiscountScope.Partner => 1,
        VolumeDiscountScope.Item => 2,
        VolumeDiscountScope.ItemType => 3,
        VolumeDiscountScope.PriceGroup => 4,
        VolumeDiscountScope.Global => 5,
        _ => 99
    };
}
