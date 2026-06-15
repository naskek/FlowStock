namespace FlowStock.Core.Models;

public static class TsdHuState
{
    public const string Unknown = "UNKNOWN";
    public const string WarehouseFree = "WAREHOUSE_FREE";
    public const string WarehouseReserved = "WAREHOUSE_RESERVED";
    public const string PlannedProduction = "PLANNED_PRODUCTION";
    public const string FilledProductionPallet = "FILLED_PRODUCTION_PALLET";
    public const string OutboundExpected = "OUTBOUND_EXPECTED";
    public const string OutboundPicked = "OUTBOUND_PICKED";
    public const string Shipped = "SHIPPED";
    public const string HistoryOnly = "HISTORY_ONLY";
    public const string Ambiguous = "AMBIGUOUS";
}

public static class TsdHuActionType
{
    public const string OpenHuCard = "OPEN_HU_CARD";
    public const string OpenFilling = "OPEN_FILLING";
    public const string OpenOutbound = "OPEN_OUTBOUND";
    public const string OpenOrder = "OPEN_ORDER";
    public const string OpenDocument = "OPEN_DOCUMENT";
    public const string ShowMessage = "SHOW_MESSAGE";
}

public sealed class TsdHuFacts
{
    public string HuCode { get; init; } = string.Empty;
    public TsdHuRegistryFact? Registry { get; init; }
    public IReadOnlyList<TsdHuStockFact> Stock { get; init; } = Array.Empty<TsdHuStockFact>();
    public IReadOnlyList<TsdHuProductionPalletFact> ProductionPallets { get; init; } = Array.Empty<TsdHuProductionPalletFact>();
    public IReadOnlyList<TsdHuReservationFact> Reservations { get; init; } = Array.Empty<TsdHuReservationFact>();
    public IReadOnlyList<TsdHuDocumentFact> Documents { get; init; } = Array.Empty<TsdHuDocumentFact>();
    public TsdHuMovementFact? LatestMovement { get; init; }
}

public sealed class TsdHuRegistryFact
{
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
}

public sealed class TsdHuStockFact
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public double Qty { get; init; }
}

public sealed class TsdHuProductionPalletFact
{
    public long PalletId { get; init; }
    public string Status { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public string PrdDocStatus { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderType { get; init; }
    public string? OrderStatus { get; init; }
    public int PalletNo { get; init; }
    public int PalletCount { get; init; }
    public DateTime? FilledAt { get; init; }
    public IReadOnlyList<TsdHuComponentFact> Components { get; init; } = Array.Empty<TsdHuComponentFact>();
}

public sealed class TsdHuComponentFact
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
}

public sealed class TsdHuReservationFact
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
}

public sealed class TsdHuDocumentFact
{
    public long DocId { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public string DocType { get; init; } = string.Empty;
    public string DocStatus { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderType { get; init; }
    public string? OrderStatus { get; init; }
    public string Direction { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public double Qty { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
}

public sealed class TsdHuMovementFact
{
    public long LedgerId { get; init; }
    public long DocId { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public string DocType { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string LocationCode { get; init; } = string.Empty;
    public double QtyDelta { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class TsdHuAction
{
    public string Type { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? HuCode { get; init; }
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public long? DocId { get; init; }
    public string? DocRef { get; init; }
    public string? Message { get; init; }
}

public sealed class TsdHuView
{
    public bool Known { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public string State { get; init; } = TsdHuState.Unknown;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TsdHuAction? CardAction { get; init; }
    public IReadOnlyList<TsdHuAction> DocumentActions { get; init; } = Array.Empty<TsdHuAction>();
    public IReadOnlyList<TsdHuStockFact> Stock { get; init; } = Array.Empty<TsdHuStockFact>();
    public IReadOnlyList<TsdHuProductionPalletFact> ProductionPallets { get; init; } = Array.Empty<TsdHuProductionPalletFact>();
    public IReadOnlyList<TsdHuReservationFact> Reservations { get; init; } = Array.Empty<TsdHuReservationFact>();
    public IReadOnlyList<TsdHuDocumentFact> Documents { get; init; } = Array.Empty<TsdHuDocumentFact>();
    public TsdHuMovementFact? LatestMovement { get; init; }
}
