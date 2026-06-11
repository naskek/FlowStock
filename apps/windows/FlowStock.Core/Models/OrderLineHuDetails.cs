namespace FlowStock.Core.Models;

public sealed class OrderLineHuDetails
{
    public IReadOnlyList<OrderLineWarehouseHuRow> WarehouseHuRows { get; init; } =
        Array.Empty<OrderLineWarehouseHuRow>();

    public IReadOnlyList<OrderLineProductionHuRow> ProductionHuRows { get; init; } =
        Array.Empty<OrderLineProductionHuRow>();

    public IReadOnlyList<OrderLineShippedHuRow> ShippedHuRows { get; init; } =
        Array.Empty<OrderLineShippedHuRow>();

    public OrderLineCoverage? Coverage { get; init; }
}

public sealed class OrderLineWarehouseHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string? LocationCode { get; init; }
    public string? LocationName { get; init; }
    public string StockStatus { get; init; } = "LEDGER_STOCK";
    public bool IsBoundToOrder { get; init; }
}

public sealed class OrderLineProductionHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public string PalletStatus { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public string? PrdRef { get; init; }
    public string? FateCode { get; init; }
    public string? FateLabel { get; init; }
    public string? FateOrderRef { get; init; }
    public string? FateDocRef { get; init; }
    public double? FateQty { get; init; }
}

public sealed class OrderLineShippedHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
}

public sealed class OrderLineCoverage
{
    public double OrderedQty { get; init; }
    public double WarehouseBoundQty { get; init; }
    public double ProductionFilledQty { get; init; }
    public double ShippedQty { get; init; }
    public double CoveredQty { get; init; }
    public double MissingQty { get; init; }
}
