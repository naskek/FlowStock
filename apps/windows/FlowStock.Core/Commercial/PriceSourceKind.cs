namespace FlowStock.Core.Commercial;

public enum PriceSourceKind
{
    Base,
    GroupRule,
    GroupOverride,
    Manual
}

public static class PriceSourceKindMapper
{
    public static string ToCode(PriceSourceKind source) => source switch
    {
        PriceSourceKind.Base => "BASE",
        PriceSourceKind.GroupRule => "GROUP_RULE",
        PriceSourceKind.GroupOverride => "GROUP_OVERRIDE",
        PriceSourceKind.Manual => "MANUAL",
        _ => "BASE"
    };

    public static PriceSourceKind? FromCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "BASE" => PriceSourceKind.Base,
        "GROUP_RULE" => PriceSourceKind.GroupRule,
        "GROUP_OVERRIDE" => PriceSourceKind.GroupOverride,
        "MANUAL" => PriceSourceKind.Manual,
        _ => null
    };

    public static string ToDisplayName(PriceSourceKind source) => source switch
    {
        PriceSourceKind.Base => "Базовая цена",
        PriceSourceKind.GroupRule => "Правило группы",
        PriceSourceKind.GroupOverride => "Индивидуальная цена",
        PriceSourceKind.Manual => "Ручная цена",
        _ => source.ToString()
    };
}

public static class CommercialPricingConstants
{
    public const string BasePriceGroupName = "Базовая цена";
}
