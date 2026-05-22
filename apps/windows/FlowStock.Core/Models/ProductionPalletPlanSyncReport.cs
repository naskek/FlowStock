namespace FlowStock.Core.Models;

public sealed class ProductionPalletPlanSyncReport
{
    public string Source { get; init; } = string.Empty;
    public long OrderId { get; init; }
    public long OrderLineId { get; init; }
    public double? OldQty { get; init; }
    public double NewQty { get; init; }
    public double FilledQty { get; init; }
    public double ActivePlannedQtyBefore { get; init; }
    public double MissingQty { get; init; }
    public double CreatedQty { get; init; }
    public double CancelledQty { get; init; }
    public double ActivePlannedQtyAfter { get; init; }
    public string Action { get; init; } = string.Empty;

    public string ToLogLine()
    {
        return $"[ProductionPalletPlanSync] source={Source} order_id={OrderId} order_line_id={OrderLineId} old_qty={Format(OldQty)} new_qty={Format(NewQty)} filled_qty={Format(FilledQty)} active_planned_qty_before={Format(ActivePlannedQtyBefore)} missing_qty={Format(MissingQty)} created_qty={Format(CreatedQty)} cancelled_qty={Format(CancelledQty)} active_planned_qty_after={Format(ActivePlannedQtyAfter)} action={Action}";
    }

    private static string Format(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "-";
    }
}
