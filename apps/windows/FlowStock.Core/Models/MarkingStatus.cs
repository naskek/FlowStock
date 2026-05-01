namespace FlowStock.Core.Models;

public enum MarkingStatus
{
    NotRequired,
    Required,
    Printed
}

public static class MarkingStatusMapper
{
    public static MarkingStatus FromString(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "REQUIRED" => MarkingStatus.Required,
            "EXCEL_GENERATED" => MarkingStatus.Printed,
            "PRINTED" => MarkingStatus.Printed,
            _ => MarkingStatus.NotRequired
        };
    }

    public static string ToString(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.Required => "REQUIRED",
            MarkingStatus.Printed => "PRINTED",
            _ => "NOT_REQUIRED"
        };
    }

    public static string ToDisplayName(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.Required => "Требуется файл ЧЗ",
            MarkingStatus.Printed => "ЧЗ готов к нанесению",
            _ => "Маркировка не требуется"
        };
    }

    public static MarkingStatus ToEffectiveStatus(MarkingStatus storedStatus, bool markingRequired)
    {
        return MarkingStatusResolver.Resolve(storedStatus, markingRequired, OrderStatus.InProgress);
    }

    public static string ToShortDisplayName(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.Required => "Требуется",
            MarkingStatus.Printed => "Готов к нанесению",
            _ => "Не требуется"
        };
    }
}
