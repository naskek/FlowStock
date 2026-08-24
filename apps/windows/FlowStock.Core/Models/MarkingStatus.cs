namespace FlowStock.Core.Models;

public enum MarkingStatus
{
    NotRequired = 0,
    NotApplied = 1,
    Applied = 2,

    // Internal compatibility sentinels for historical persisted values. API output
    // always goes through the canonical mapper below.
    Required = 3,
    Printed = 4
}

public static class MarkingStatusMapper
{
    public static MarkingStatus FromString(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "NOT_APPLIED" => MarkingStatus.NotApplied,
            "REQUIRED" => MarkingStatus.NotApplied,
            "APPLIED" => MarkingStatus.Applied,
            "EXCEL_GENERATED" => MarkingStatus.NotApplied,
            "PRINTED" => MarkingStatus.NotApplied,
            _ => MarkingStatus.NotRequired
        };
    }

    public static string ToString(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.NotApplied or MarkingStatus.Required or MarkingStatus.Printed => "NOT_APPLIED",
            MarkingStatus.Applied => "APPLIED",
            _ => "NOT_REQUIRED"
        };
    }

    public static string ToDisplayName(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.NotApplied or MarkingStatus.Required or MarkingStatus.Printed => "Маркировка не проведена",
            MarkingStatus.Applied => "Маркировка проведена",
            _ => string.Empty
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
            MarkingStatus.NotApplied or MarkingStatus.Required or MarkingStatus.Printed => "Маркировка не проведена",
            MarkingStatus.Applied => "Маркировка проведена",
            _ => string.Empty
        };
    }
}
