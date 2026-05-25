namespace FlowStock.Core.Commercial;

public enum VatMode
{
    Included,
    Excluded,
    NoVat
}

public static class VatModeMapper
{
    public static string ToCode(VatMode mode) => mode switch
    {
        VatMode.Included => "INCLUDED",
        VatMode.Excluded => "EXCLUDED",
        VatMode.NoVat => "NO_VAT",
        _ => "INCLUDED"
    };

    public static VatMode FromCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "INCLUDED" => VatMode.Included,
        "EXCLUDED" => VatMode.Excluded,
        "NO_VAT" => VatMode.NoVat,
        _ => VatMode.Included
    };

    public static string ToDisplayName(VatMode mode) => mode switch
    {
        VatMode.Included => "С НДС",
        VatMode.Excluded => "Без НДС",
        VatMode.NoVat => "НДС не применяется",
        _ => mode.ToString()
    };
}
