namespace FlowStock.Core.Models;

public static class ProductionPalletStatus
{
    public const string Planned = "PLANNED";
    public const string Printed = "PRINTED";
    public const string Filled = "FILLED";
    public const string Cancelled = "CANCELLED";
}

public sealed class ProductionPallet
{
    public long Id { get; init; }
    public long PrdDocId { get; init; }
    public long DocLineId { get; init; }
    public long? OrderId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public double PlannedQty { get; init; }
    public long? ToLocationId { get; init; }
    public string? ToLocationCode { get; init; }
    public string Status { get; init; } = ProductionPalletStatus.Planned;
    public int PalletNo { get; init; }
    public int PalletCount { get; init; }
    public DateTime? PrintedAt { get; init; }
    public DateTime? FilledAt { get; init; }
    public string? FilledByDeviceId { get; init; }
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<ProductionPalletComponentLine> Lines { get; init; } = Array.Empty<ProductionPalletComponentLine>();
    public bool IsMixedPallet => Lines.Count > 1;
}

public sealed class ProductionPalletComponentLine
{
    public long Id { get; init; }
    public long ProductionPalletId { get; init; }
    public long DocLineId { get; init; }
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string Uom { get; init; } = "шт";
    public double PlannedQty { get; init; }
    public double FilledQty { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class ProductionPalletSummary
{
    public int PlannedPalletCount { get; init; }
    public double PlannedQty { get; init; }
    public int FilledPalletCount { get; init; }
    public double FilledQty { get; init; }
    public int RemainingPalletCount { get; init; }
    public double RemainingQty { get; init; }
}

public sealed class ProductionPalletLineSummary
{
    public long? OrderLineId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double OrderedQty { get; init; }
    public int PlannedPalletCount { get; init; }
    public double PlannedQty { get; init; }
    public int FilledPalletCount { get; init; }
    public double FilledQty { get; init; }
    public int RemainingPalletCount { get; init; }
    public double RemainingQty { get; init; }
}

public sealed class ProductionPalletDocument
{
    public long PrdDocId { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
    public IReadOnlyList<ProductionPalletLineSummary> Lines { get; init; } = Array.Empty<ProductionPalletLineSummary>();
    public IReadOnlyList<ProductionPallet> Pallets { get; init; } = Array.Empty<ProductionPallet>();
}

public sealed class ProductionPalletWorkItem
{
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public string PrdStatus { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
}

public sealed class ProductionFillingOrder
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderTypeDisplay { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string OrderStatusDisplay { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public long? PrdDocId { get; init; }
    public string? PrdDocRef { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
}

public sealed class ProductionFillingContext
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string OrderType { get; init; } = string.Empty;
    public string OrderTypeDisplay { get; init; } = string.Empty;
    public string OrderStatus { get; init; } = string.Empty;
    public string OrderStatusDisplay { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public ProductionPalletDocument Document { get; init; } = new();
}

public sealed class ProductionPalletOrderPlanResult
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public bool WasExisting { get; init; }
    public ProductionPalletSummary Summary { get; init; } = new();
    public ProductionPalletDocument Document { get; init; } = new();
}

public sealed class ProductionPalletPrintRow
{
    public long PalletId { get; init; }
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public long PrdDocId { get; init; }
    public string PrdRef { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
    public int PalletNo { get; init; }
    public int PalletCount { get; init; }
    public string StoragePlace { get; init; } = string.Empty;
    public DateTime? ProductionDate { get; init; }
    public string Comment { get; init; } = string.Empty;
    public bool IsMixedPallet { get; init; }
    public string Composition { get; init; } = string.Empty;
    public IReadOnlyList<ProductionPalletPrintLine> Lines { get; init; } = Array.Empty<ProductionPalletPrintLine>();
    public string Status { get; init; } = ProductionPalletStatus.Planned;
}

public sealed class ProductionPalletPrintLine
{
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
}

public sealed class ProductionPalletScanResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool AlreadyFilled { get; init; }
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public long PrdDocId { get; init; }
    public string PrdDocRef { get; init; } = string.Empty;
    public long PalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemBrand { get; init; }
    public string BaseUom { get; init; } = "шт";
    public double PlannedQty { get; init; }
    public bool IsMixedPallet { get; init; }
    public IReadOnlyList<ProductionPalletScanLine> Lines { get; init; } = Array.Empty<ProductionPalletScanLine>();
    public int PalletIndex { get; init; }
    public int PalletCount { get; init; }
    public string PalletStatus { get; init; } = ProductionPalletStatus.Planned;
    public ProductionPalletDocument? Document { get; init; }

    public static ProductionPalletScanResult Failure(string error)
    {
        return new ProductionPalletScanResult { Success = false, Error = error };
    }
}

public sealed class ProductionPalletScanLine
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
}

public sealed class ProductionPalletFillResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool AlreadyFilled { get; init; }
    public ProductionPallet? Pallet { get; init; }
    public ProductionPalletDocument? Document { get; init; }

    public static ProductionPalletFillResult Failure(string error)
    {
        return new ProductionPalletFillResult { Success = false, Error = error };
    }
}
