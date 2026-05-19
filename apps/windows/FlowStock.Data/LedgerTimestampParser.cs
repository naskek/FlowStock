using System.Globalization;

namespace FlowStock.Data;

public static class LedgerTimestampParser
{
    public static DateTime? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        return DateTime.TryParse(value, out parsed) ? parsed : null;
    }
}
