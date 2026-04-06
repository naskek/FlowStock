namespace FlowStock.Core.Services.Marking;

public static class MarkingCodeNormalizer
{
    public static string NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && trimmed[0] == '"'
            && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    public static string? NormalizeGtin(string? value)
    {
        var normalized = NormalizeCode(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var ch in normalized)
        {
            if (!char.IsDigit(ch))
            {
                return null;
            }
        }

        return normalized.Length switch
        {
            14 => normalized,
            13 => "0" + normalized,
            _ => null
        };
    }
}
