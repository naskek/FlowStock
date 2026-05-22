namespace FlowStock.Core.Models;

public sealed class OrderLineView
{
    private const double QtyTolerance = 0.000001d;

    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public double QtyOrdered { get; set; }
    public ProductionLinePurpose ProductionPurpose { get; set; } = ProductionLinePurpose.InternalStock;
    public string? ProductionPalletGroup { get; set; }
    public int MixedPalletGroupNumber { get; set; } = 1;
    public string ProductionHuCodes { get; set; } = string.Empty;
    public double QtyShipped { get; set; }
    public double QtyProduced { get; set; }
    public double QtyRemaining { get; set; }
    public double QtyAvailable { get; set; }
    public double CanShipNow { get; set; }
    public double Shortage { get; set; }
    public int PlannedPalletCount { get; set; }
    public int FilledPalletCount { get; set; }
    public double PlannedPalletQty { get; set; }
    public double FilledPalletQty { get; set; }
    public bool LineFullyShipped { get; set; }
    public bool HidePalletFillIndicator { get; set; }
    public bool ShowPalletCompletedIcon { get; set; }
    public bool BlockingFillRequired { get; set; }
    public string FulfillmentStatus { get; set; } = string.Empty;
    public string? PalletFillLabel { get; set; }
    public string PalletFillTone { get; set; } = "neutral";
    public string? PalletFillTitle { get; set; }
    public string ProductionPurposeDisplay => ProductionLinePurposeMapper.ToDisplayName(ProductionPurpose);
    public bool IsMixedPalletLine => !string.IsNullOrWhiteSpace(ProductionPalletGroup);
    public string ProductionPalletGroupDisplay => string.IsNullOrWhiteSpace(ProductionPalletGroup) ? string.Empty : ProductionPalletGroup!;
    public string HuCoverageTone
    {
        get
        {
            if (ProductionPurpose != ProductionLinePurpose.InternalStock || QtyOrdered <= QtyTolerance)
            {
                return "neutral";
            }

            return QtyProduced + QtyTolerance >= QtyOrdered ? "covered" : "neutral";
        }
    }

    public string? HuCoverageToolTip
    {
        get
        {
            if (HuCoverageTone != "covered")
            {
                return null;
            }

            var itemName = string.IsNullOrWhiteSpace(ItemName) ? "Товар без названия" : ItemName.Trim();
            return $"{itemName}: выпущено {FormatQty(QtyProduced)} из {FormatQty(QtyOrdered)}";
        }
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}

