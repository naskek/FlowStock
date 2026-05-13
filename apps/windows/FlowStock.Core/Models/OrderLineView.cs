namespace FlowStock.Core.Models;

public sealed class OrderLineView
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public double QtyOrdered { get; set; }
    public ProductionLinePurpose ProductionPurpose { get; set; } = ProductionLinePurpose.InternalStock;
    public string? ProductionPalletGroup { get; set; }
    public string ProductionHuCodes { get; set; } = string.Empty;
    public double QtyShipped { get; set; }
    public double QtyProduced { get; set; }
    public double QtyRemaining { get; set; }
    public double QtyAvailable { get; set; }
    public double CanShipNow { get; set; }
    public double Shortage { get; set; }
    public string ProductionPurposeDisplay => ProductionLinePurposeMapper.ToDisplayName(ProductionPurpose);
    public bool IsMixedPalletLine => !string.IsNullOrWhiteSpace(ProductionPalletGroup);
    public string ProductionPalletGroupDisplay => string.IsNullOrWhiteSpace(ProductionPalletGroup) ? string.Empty : ProductionPalletGroup!;
}

