namespace FlowStock.Core.Models;

public enum MarkingStatus
{
    NotRequired,
    Required,
    ExcelGenerated,
    Printed
}

public static class MarkingStatusMapper
{
    public static MarkingStatus FromString(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "REQUIRED" => MarkingStatus.Required,
            "EXCEL_GENERATED" => MarkingStatus.ExcelGenerated,
            "PRINTED" => MarkingStatus.Printed,
            _ => MarkingStatus.NotRequired
        };
    }

    public static string ToString(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.Required => "REQUIRED",
            MarkingStatus.ExcelGenerated => "EXCEL_GENERATED",
            MarkingStatus.Printed => "PRINTED",
            _ => "NOT_REQUIRED"
        };
    }

    public static string ToDisplayName(MarkingStatus status)
    {
        return status switch
        {
            MarkingStatus.Required => "Требуется файл ЧЗ",
            MarkingStatus.ExcelGenerated => "Файл ЧЗ сформирован",
            MarkingStatus.Printed => "Маркировка проведена",
            _ => "Маркировка не требуется"
        };
    }
}
