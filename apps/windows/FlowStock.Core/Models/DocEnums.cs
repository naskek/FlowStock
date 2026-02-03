namespace FlowStock.Core.Models;

public enum DocType
{
    Inbound,
    WriteOff,
    Move,
    Inventory,
    Outbound
}

public enum DocStatus
{
    Draft,
    Closed
}

public static class DocTypeMapper
{
    public static DocType? FromOpString(string? op)
    {
        return op?.Trim().ToUpperInvariant() switch
        {
            "INBOUND" => DocType.Inbound,
            "WRITE_OFF" => DocType.WriteOff,
            "MOVE" => DocType.Move,
            "INVENTORY" => DocType.Inventory,
            "OUTBOUND" => DocType.Outbound,
            _ => null
        };
    }

    public static string ToOpString(DocType type)
    {
        return type switch
        {
            DocType.Inbound => "INBOUND",
            DocType.WriteOff => "WRITE_OFF",
            DocType.Move => "MOVE",
            DocType.Inventory => "INVENTORY",
            DocType.Outbound => "OUTBOUND",
            _ => "UNKNOWN"
        };
    }

    public static DocStatus? StatusFromString(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "DRAFT" => DocStatus.Draft,
            "CLOSED" => DocStatus.Closed,
            _ => null
        };
    }

    public static string StatusToString(DocStatus status)
    {
        return status switch
        {
            DocStatus.Draft => "DRAFT",
            DocStatus.Closed => "CLOSED",
            _ => "UNKNOWN"
        };
    }

    public static string ToDisplayName(DocType type)
    {
        return type switch
        {
            DocType.Inbound => "Приемка",
            DocType.WriteOff => "Списание",
            DocType.Move => "Перемещение",
            DocType.Inventory => "Инвентаризация",
            DocType.Outbound => "Отгрузка",
            _ => "Неизвестно"
        };
    }

    public static string StatusToDisplayName(DocStatus status)
    {
        return status switch
        {
            DocStatus.Draft => "Черновик",
            DocStatus.Closed => "Проведена",
            _ => "Неизвестно"
        };
    }
}

