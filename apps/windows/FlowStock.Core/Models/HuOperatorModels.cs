namespace FlowStock.Core.Models;

public static class ProductionTaskSemanticCode
{
    public const string LabelNotPrinted = "LABEL_NOT_PRINTED";
    public const string AwaitingFill = "AWAITING_FILL";
    public const string Filling = "FILLING";
    public const string ReleaseNotPosted = "RELEASE_NOT_POSTED";
}

public static class OperationalHuSemanticCode
{
    public const string AwaitingShipment = "AWAITING_SHIPMENT";
    public const string Reserved = "RESERVED";
    public const string OnStock = "ON_STOCK";
    public const string Shipped = "SHIPPED";
    public const string Inconsistent = "INCONSISTENT";
}

public static class HuOperatorDiagnosticCode
{
    public const string PartialClosedOutboundWithRemainder = "PARTIAL_CLOSED_OUTBOUND_WITH_REMAINDER";
    public const string ConflictingActiveReservations = "CONFLICTING_ACTIVE_RESERVATIONS";
    public const string MixedOperationalTargetConflict = "MIXED_OPERATIONAL_TARGET_CONFLICT";
    public const string ProductionLedgerContradiction = "PRODUCTION_LEDGER_CONTRADICTION";
    public const string CorrectionLineageUncertain = "CORRECTION_LINEAGE_UNCERTAIN";
}

public sealed class HuOperatorFacts
{
    public string HuCode { get; init; } = string.Empty;
    public bool RegistryKnown { get; init; }
    public IReadOnlyList<HuOperatorStockFact> Stock { get; init; } = Array.Empty<HuOperatorStockFact>();
    public IReadOnlyList<HuOperatorProductionPalletFact> ProductionPallets { get; init; } =
        Array.Empty<HuOperatorProductionPalletFact>();
    public IReadOnlyList<HuOperatorReservationFact> Reservations { get; init; } =
        Array.Empty<HuOperatorReservationFact>();
    public IReadOnlyList<HuOperatorOutboundFact> Outbound { get; init; } = Array.Empty<HuOperatorOutboundFact>();
    public IReadOnlyList<HuOperatorLedgerMovementFact> LedgerMovements { get; init; } =
        Array.Empty<HuOperatorLedgerMovementFact>();
}

public sealed class HuOperatorStockFact
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public string? LocationName { get; init; }
    public double Qty { get; init; }
}

public sealed class HuOperatorProductionPalletFact
{
    public long PalletId { get; init; }
    public string Status { get; init; } = string.Empty;
    public long? OwnerOrderId { get; init; }
    public string? OwnerOrderRef { get; init; }
    public string? OwnerOrderType { get; init; }
    public string? OwnerOrderStatus { get; init; }
    public IReadOnlyList<HuOperatorComponentFact> Components { get; init; } =
        Array.Empty<HuOperatorComponentFact>();
}

public sealed class HuOperatorComponentFact
{
    public long? OrderLineId { get; init; }
    public long? OrderLineOrderId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
}

public sealed class HuOperatorReservationFact
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double Qty { get; init; }
}

public sealed class HuOperatorOutboundFact
{
    public long DocumentId { get; init; }
    public string DocumentRef { get; init; } = string.Empty;
    public string DocumentStatus { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? OrderType { get; init; }
    public string? OrderStatus { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public double Qty { get; init; }
    public DateTime? ClosedAt { get; init; }
    public bool IsEffective { get; init; } = true;
}

public sealed class HuOperatorLedgerMovementFact
{
    public long LedgerId { get; init; }
    public DateTime Timestamp { get; init; }
    public long DocumentId { get; init; }
    public string DocumentRef { get; init; } = string.Empty;
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentStatus { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Uom { get; init; } = "шт";
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public string? LocationName { get; init; }
    public double QtyDelta { get; init; }
}

public abstract record HuOperatorClassification(string HuCode);

public sealed record HuOperatorNoCurrentClassification(string HuCode)
    : HuOperatorClassification(HuCode);

public sealed record HuOperatorProductionClassification(
    string HuCode,
    string StateCode,
    int? CompletedComponents = null,
    int? TotalComponents = null)
    : HuOperatorClassification(HuCode);

public sealed record HuOperatorOperationalClassification(
    string HuCode,
    string StateCode,
    IReadOnlyList<HuOperatorDiagnosticReason>? DiagnosticReasons = null,
    HuOperatorOrderReference? ReservationTarget = null,
    HuOperatorOrderReference? ShipmentTarget = null,
    long? CurrentShipmentDocumentId = null)
    : HuOperatorClassification(HuCode);

public sealed record HuOperatorOrderReference(long OrderId, string OrderRef);

public sealed record HuOperatorDocumentReference(long DocumentId, string DocumentRef);

public sealed record HuOperatorDiagnosticReason(
    string Code,
    string Message,
    IReadOnlyList<HuOperatorOrderReference>? RelatedOrders = null,
    IReadOnlyList<HuOperatorDocumentReference>? RelatedDocuments = null);

public sealed record HuSemanticStatePresentation(string Code, string Label);

public sealed record HuProductionProgressPresentation(int CompletedComponents, int TotalComponents);

public sealed record HuLocationPresentation(long Id, string Code, string? Name);

public sealed record HuComponentPresentation(
    long ItemId,
    string ItemName,
    double Qty,
    string Uom);

public sealed class ProductionTaskPresentation
{
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
    public HuSemanticStatePresentation State { get; init; } = new(string.Empty, string.Empty);
    public HuProductionProgressPresentation? Progress { get; init; }
    public IReadOnlyList<HuComponentPresentation> Components { get; init; } =
        Array.Empty<HuComponentPresentation>();
}

public sealed class OperationalHuPresentation
{
    public string HuCode { get; init; } = string.Empty;
    public double? Qty { get; init; }
    public string? Uom { get; init; }
    public HuSemanticStatePresentation State { get; init; } = new(string.Empty, string.Empty);
    public IReadOnlyList<HuComponentPresentation> Components { get; init; } =
        Array.Empty<HuComponentPresentation>();
    public HuLocationPresentation? Location { get; init; }
    public HuOperatorOrderReference? ReservationTarget { get; init; }
    public HuOperatorOrderReference? ShipmentTarget { get; init; }
    public bool IsMixed { get; init; }
    public IReadOnlyList<HuOperatorDiagnosticReason>? Diagnostics { get; init; }
}

public sealed class OrderLineHuPresentation
{
    public IReadOnlyList<ProductionTaskPresentation> ProductionTasks { get; init; } =
        Array.Empty<ProductionTaskPresentation>();
    public IReadOnlyList<OperationalHuPresentation> OperationalHus { get; init; } =
        Array.Empty<OperationalHuPresentation>();
}

public sealed class GlobalHuOperatorPresentation
{
    public ProductionTaskPresentation? ProductionTask { get; init; }
    public OperationalHuPresentation? OperationalHu { get; init; }
}

public sealed class GlobalHuOperatorReadModel
{
    public bool Known { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public GlobalHuOperatorPresentation OperatorPresentation { get; init; } = new();
    public bool HistoryAvailable { get; init; }
}
