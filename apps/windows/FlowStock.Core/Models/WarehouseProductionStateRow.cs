namespace FlowStock.Core.Models;

public sealed class WarehouseProductionStateRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public string? ItemType { get; init; }
    public string? Brand { get; init; }
    public string BaseUom { get; init; } = "шт";
    public double StockQty { get; init; }
    public double FreeQty { get; init; }
    public double ReservedQty { get; init; }
    public double MinStockQty { get; init; }
    public double BelowMinQty { get; init; }
    public double CustomerOpenDemandQty { get; init; }
    public double CustomerRemainingToShipQty { get; init; }
    public double InternalOpenQty { get; init; }
    public double InternalRemainingQty { get; init; }
    public double PrdPlannedQty { get; init; }
    public double PrdFilledQty { get; init; }
    public int PalletPlannedCount { get; init; }
    public int PalletFilledCount { get; init; }
    public double RemainingNeedQty { get; init; }
    public string NeedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public WarehouseProductionStateNeedBreakdownRow NeedBreakdown { get; init; } = new();
    public IReadOnlyList<WarehouseProductionStateHuRow> HuRows { get; init; } = Array.Empty<WarehouseProductionStateHuRow>();
    public IReadOnlyList<WarehouseProductionStateCustomerOrderRow> CustomerOrders { get; init; } = Array.Empty<WarehouseProductionStateCustomerOrderRow>();
    public IReadOnlyList<WarehouseProductionStateInternalOrderRow> InternalOrders { get; init; } = Array.Empty<WarehouseProductionStateInternalOrderRow>();
    public IReadOnlyList<WarehouseProductionStatePalletRow> ProductionReceipts { get; init; } = Array.Empty<WarehouseProductionStatePalletRow>();
}

public sealed class WarehouseProductionStateHuRow
{
    public string Location { get; init; } = string.Empty;
    public long LocationId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public long? OriginInternalOrderId { get; init; }
    public string? OriginInternalOrderRef { get; init; }
    public long? ReservedCustomerOrderId { get; init; }
    public string? ReservedCustomerOrderRef { get; init; }
    public long? ReservedCustomerId { get; init; }
    public string? ReservedCustomerName { get; init; }
    public string StockStatus { get; init; } = string.Empty;
}

public sealed class WarehouseProductionStateCustomerOrderRow
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public string Status { get; init; } = string.Empty;
    public double QtyOrdered { get; init; }
    public double ShippedQty { get; init; }
    public double RemainingQty { get; init; }
}

public sealed class WarehouseProductionStateInternalOrderRow
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double QtyOrdered { get; init; }
    public double ProducedQty { get; init; }
    public double RemainingQty { get; init; }
}

public sealed class WarehouseProductionStatePalletRow
{
    public long PrdDocId { get; init; }
    public string PrdRef { get; init; } = string.Empty;
    public long PalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public string PalletStatus { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public string StockEffect { get; init; } = string.Empty;
    public bool IsMixedPallet { get; init; }
    public string Composition { get; init; } = string.Empty;
    public string? Location { get; init; }
}

public sealed class WarehouseProductionStateNeedBreakdownRow
{
    public double DemandToCloseCustomerOrders { get; init; }
    public double DemandToMinStock { get; init; }
    public double AlreadyPlannedInternal { get; init; }
    public double AlreadyPlannedPrd { get; init; }
    public double RemainingToCreate { get; init; }
}

public sealed class WarehouseProductionStatePalletAggregate
{
    public IReadOnlyList<WarehouseProductionStatePalletRow> Rows { get; init; } = Array.Empty<WarehouseProductionStatePalletRow>();
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public int PlannedCount { get; init; }
    public int FilledCount { get; init; }
    public bool HasFilledWithoutLedger { get; init; }
    public bool HasStalePalletAfterFullShipment { get; init; }

    public static WarehouseProductionStatePalletAggregate Empty { get; } = new();
}
