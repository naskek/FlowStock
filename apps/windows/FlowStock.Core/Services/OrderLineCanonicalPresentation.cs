using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Синхронизация полей строки заказа из canonical API после сохранения (WPF и тесты).
/// </summary>
public static class OrderLineCanonicalPresentation
{
    public static void ApplyPersistedLine(OrderLineView target, OrderLineView source, OrderType orderType)
    {
        target.QtyOrdered = source.QtyOrdered;
        target.QtyAvailable = source.QtyAvailable;
        target.QtyProduced = source.QtyProduced;
        target.QtyShipped = source.QtyShipped;
        target.QtyRemaining = source.QtyRemaining;
        target.CanShipNow = orderType == OrderType.Internal ? 0 : source.CanShipNow;
        target.Shortage = orderType == OrderType.Internal ? 0 : source.Shortage;
        target.ProductionHuCodes = source.ProductionHuCodes ?? string.Empty;
        target.PlannedPalletCount = source.PlannedPalletCount;
        target.FilledPalletCount = source.FilledPalletCount;
        target.PlannedPalletQty = source.PlannedPalletQty;
        target.FilledPalletQty = source.FilledPalletQty;
        target.LineFullyShipped = source.LineFullyShipped;
        target.HidePalletFillIndicator = source.HidePalletFillIndicator;
        target.ShowPalletCompletedIcon = source.ShowPalletCompletedIcon;
        target.BlockingFillRequired = source.BlockingFillRequired;
        target.FulfillmentStatus = source.FulfillmentStatus;
        target.PalletFillLabel = source.PalletFillLabel;
        target.PalletFillTone = source.PalletFillTone;
        target.PalletFillTitle = source.PalletFillTitle;
        target.NotifyPresentationChanged();
    }

    public static string ResolveProductionHuCodesDisplay(
        string? productionHuCodesDisplay,
        IReadOnlyList<string>? productionHuCodes)
    {
        if (!string.IsNullOrWhiteSpace(productionHuCodesDisplay))
        {
            return productionHuCodesDisplay.Trim();
        }

        if (productionHuCodes == null || productionHuCodes.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", productionHuCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase));
    }
}
