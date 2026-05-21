namespace FlowStock.Core.Services;

public static class ProductionPalletGroupHelper
{
    public const string Prefix = "MIX-";

    public static string Format(int groupNumber)
    {
        return groupNumber < 1 ? $"{Prefix}1" : $"{Prefix}{groupNumber}";
    }

    public static int ParseNumber(string? groupCode)
    {
        if (string.IsNullOrWhiteSpace(groupCode))
        {
            return 1;
        }

        var trimmed = groupCode.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var suffix = trimmed[Prefix.Length..];
        return int.TryParse(suffix, out var number) && number > 0 ? number : 1;
    }
}
