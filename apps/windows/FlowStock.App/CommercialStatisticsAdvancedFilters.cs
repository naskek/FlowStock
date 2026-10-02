using System.Globalization;

namespace FlowStock.App;

internal static class CommercialStatisticsAdvancedFilters
{
    public static string? NormalizeItemNameContains(string? value)
    {
        var normalized = string.Join(
            ' ',
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? NormalizeGtinsCsv(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(RemoveWhitespace)
            .Where(gtin => !string.IsNullOrWhiteSpace(gtin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? null : string.Join(',', normalized);
    }

    public static (DateTime From, DateTime To) MonthPeriod(DateTime value)
    {
        var from = new DateTime(value.Year, value.Month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    public static string FormatMonth(DateTime value)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return $"{value.ToString("MMMM", culture)} {value.Year}";
    }

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
