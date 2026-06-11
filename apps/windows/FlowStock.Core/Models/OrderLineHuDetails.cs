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

public sealed class OrderLineHuDetailsTiming
{
    public long? GetOrderLinesMs { get; set; }
    public long? BuildWarehouseRowsMs { get; set; }
    public long? HuFateMs { get; set; }
    public long? BuildProductionRowsMs { get; set; }
    public long? BuildShippedRowsMs { get; set; }
    public long? ConfirmedReceiptLedgerTotalsMs { get; set; }
    public long? CustomerCoverageMs { get; set; }
    public long? FinalMappingMs { get; set; }
    public long? TotalMs { get; set; }
}

public sealed class OrderLineHuFateTiming
{
    public long? GetOrdersMs { get; set; }
    public int? OrdersCount { get; set; }
    public long? GetDocsMs { get; set; }
    public int? DocsCount { get; set; }
    public long? GetHuStockRowsMs { get; set; }
    public int? HuStockRowsCount { get; set; }
    public long? BuildSourcesMs { get; set; }
    public int? SourcesCount { get; set; }
    public long? BuildReservationsMs { get; set; }
    public int? ReservationsCount { get; set; }
    public long? BuildShipmentsMs { get; set; }
    public int? ShipmentsCount { get; set; }
    public long? FinalRowsMs { get; set; }
    public int? FinalRowsCount { get; set; }
    public long? TotalMs { get; set; }
}
