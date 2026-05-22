using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowStock.Core.Models;

public sealed class OrderLineView : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;
    private double _qtyOrdered;
    private string _productionHuCodes = string.Empty;
    private double _qtyShipped;
    private double _qtyProduced;
    private double _qtyRemaining;
    private double _plannedPalletQty;
    private double _filledPalletQty;
    private int _plannedPalletCount;
    private int _filledPalletCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public double QtyOrdered
    {
        get => _qtyOrdered;
        set => SetField(ref _qtyOrdered, value);
    }
    public ProductionLinePurpose ProductionPurpose { get; set; } = ProductionLinePurpose.InternalStock;
    public string? ProductionPalletGroup { get; set; }
    public int MixedPalletGroupNumber { get; set; } = 1;
    public string ProductionHuCodes
    {
        get => _productionHuCodes;
        set => SetField(ref _productionHuCodes, value ?? string.Empty);
    }

    public double QtyShipped
    {
        get => _qtyShipped;
        set => SetField(ref _qtyShipped, value);
    }

    public double QtyProduced
    {
        get => _qtyProduced;
        set => SetField(ref _qtyProduced, value);
    }

    public double QtyRemaining
    {
        get => _qtyRemaining;
        set => SetField(ref _qtyRemaining, value);
    }
    public double QtyAvailable { get; set; }
    public double CanShipNow { get; set; }
    public double Shortage { get; set; }
    public int PlannedPalletCount
    {
        get => _plannedPalletCount;
        set => SetField(ref _plannedPalletCount, value);
    }

    public int FilledPalletCount
    {
        get => _filledPalletCount;
        set => SetField(ref _filledPalletCount, value);
    }

    public double PlannedPalletQty
    {
        get => _plannedPalletQty;
        set => SetField(ref _plannedPalletQty, value);
    }

    public double FilledPalletQty
    {
        get => _filledPalletQty;
        set => SetField(ref _filledPalletQty, value);
    }
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

    public void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(QtyOrdered));
        OnPropertyChanged(nameof(ProductionHuCodes));
        OnPropertyChanged(nameof(QtyShipped));
        OnPropertyChanged(nameof(QtyProduced));
        OnPropertyChanged(nameof(QtyRemaining));
        OnPropertyChanged(nameof(PlannedPalletCount));
        OnPropertyChanged(nameof(FilledPalletCount));
        OnPropertyChanged(nameof(PlannedPalletQty));
        OnPropertyChanged(nameof(FilledPalletQty));
        OnPropertyChanged(nameof(HuCoverageTone));
        OnPropertyChanged(nameof(HuCoverageToolTip));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}

