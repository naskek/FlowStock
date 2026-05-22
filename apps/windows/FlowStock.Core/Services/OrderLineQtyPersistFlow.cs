using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Формирование payload и диагностика persist изменения qty строки (WPF/API).
/// </summary>
public static class OrderLineQtyPersistFlow
{
    public static IReadOnlyList<OrderLineView> BuildLinesForPersist(
        IReadOnlyList<OrderLineView> currentLines,
        long orderLineId,
        double newQty)
    {
        return currentLines
            .Select(line => line.Id == orderLineId
                ? CloneLineWithQty(line, newQty)
                : line)
            .ToList();
    }

    public static string FormatQtyEditLogLine(OrderLineQtyEditLogEntry entry)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return $"[OrderLineQtyEdit] order_id={entry.OrderId} order_line_id={entry.OrderLineId} old_qty={entry.OldQty.ToString("0.###", culture)} new_qty={entry.NewQty.ToString("0.###", culture)} payload_qty={entry.PayloadQty.ToString("0.###", culture)} put_status={entry.PutStatus} reload_started={entry.ReloadStarted} reload_finished={entry.ReloadFinished} hu_codes_before=\"{entry.HuCodesBefore ?? string.Empty}\" hu_codes_after=\"{entry.HuCodesAfter ?? string.Empty}\"";
    }

    private static OrderLineView CloneLineWithQty(OrderLineView line, double newQty)
    {
        return new OrderLineView
        {
            Id = line.Id,
            OrderId = line.OrderId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            Barcode = line.Barcode,
            Gtin = line.Gtin,
            QtyOrdered = newQty,
            ProductionPurpose = line.ProductionPurpose,
            ProductionPalletGroup = line.ProductionPalletGroup,
            MixedPalletGroupNumber = line.MixedPalletGroupNumber,
            ProductionHuCodes = line.ProductionHuCodes,
            QtyShipped = line.QtyShipped,
            QtyProduced = line.QtyProduced,
            QtyRemaining = line.QtyRemaining,
            QtyAvailable = line.QtyAvailable,
            CanShipNow = line.CanShipNow,
            Shortage = line.Shortage,
            PlannedPalletCount = line.PlannedPalletCount,
            FilledPalletCount = line.FilledPalletCount,
            PlannedPalletQty = line.PlannedPalletQty,
            FilledPalletQty = line.FilledPalletQty,
            LineFullyShipped = line.LineFullyShipped,
            HidePalletFillIndicator = line.HidePalletFillIndicator,
            ShowPalletCompletedIcon = line.ShowPalletCompletedIcon,
            BlockingFillRequired = line.BlockingFillRequired,
            FulfillmentStatus = line.FulfillmentStatus,
            PalletFillLabel = line.PalletFillLabel,
            PalletFillTone = line.PalletFillTone,
            PalletFillTitle = line.PalletFillTitle
        };
    }
}

public sealed record OrderLineQtyEditLogEntry
{
    public long OrderId { get; init; }
    public long OrderLineId { get; init; }
    public double OldQty { get; init; }
    public double NewQty { get; init; }
    public double PayloadQty { get; init; }
    public int PutStatus { get; init; }
    public bool ReloadStarted { get; init; }
    public bool ReloadFinished { get; init; }
    public string? HuCodesBefore { get; init; }
    public string? HuCodesAfter { get; init; }
}
