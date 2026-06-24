using System.Globalization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class OrderApiMapper
{
    public static object MapOrder(
        Order order,
        bool? hasShipmentRemaining = null,
        bool? hasProductionPalletPlan = null,
        bool? needsProductionPalletPlan = null,
        ProductionPalletSummary? palletSummary = null,
        string? palletPlanStatus = null,
        OrderPalletFillPresentation? palletFill = null,
        OrderShipmentProgress? shipmentProgress = null)
    {
        var markingStatus = order.MarkingCompleted
            ? MarkingStatus.Printed
            : order.EffectiveMarkingStatus;
        var markingLabel = order.MarkingLabel;
        palletSummary ??= new ProductionPalletSummary();
        palletFill ??= OrderPalletFillPresentationService.ResolveOrderFill(
            order,
            needsProductionPalletPlan ?? false,
            hasProductionPalletPlan ?? false,
            palletSummary);
        var statusDisplay = shipmentProgress?.IsPartiallyShipped == true
            ? "Частично отгружено"
            : OrderStatusMapper.StatusToDisplayName(order.Status, order.Type);

        return new
        {
            id = order.Id,
            order_ref = order.OrderRef,
            order_type = OrderStatusMapper.TypeToString(order.Type),
            partner_id = order.PartnerId,
            partner_name = order.PartnerName,
            partner_code = order.PartnerCode,
            due_date = order.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            order_status = OrderStatusMapper.StatusToString(order.Status),
            order_status_display = statusDisplay,
            status = statusDisplay,
            comment = order.Comment,
            bind_reserved_stock = order.UseReservedStock,
            marking_status = MarkingStatusMapper.ToString(markingStatus),
            marking_required = order.MarkingRequired,
            marking_applies = order.MarkingApplies,
            marking_completed = order.MarkingCompleted,
            marking_label = markingLabel,
            marking_effective_status = MarkingStatusMapper.ToString(markingStatus),
            marking_status_display = markingLabel,
            marking_status_label = markingLabel,
            marking_excel_generated_at = order.MarkingExcelGeneratedAt?.ToString("O", CultureInfo.InvariantCulture),
            marking_printed_at = order.MarkingPrintedAt?.ToString("O", CultureInfo.InvariantCulture),
            created_at = order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            shipped_at = order.ShippedAt?.ToString("O", CultureInfo.InvariantCulture),
            has_shipment_remaining = hasShipmentRemaining,
            shipment_ordered_qty = shipmentProgress?.OrderedQty ?? 0d,
            shipment_shipped_qty = shipmentProgress?.ShippedQty ?? 0d,
            shipment_remaining_qty = shipmentProgress?.RemainingQty ?? 0d,
            is_partially_shipped = shipmentProgress?.IsPartiallyShipped ?? false,
            active_order_control_ref = order.ActiveOrderControlRef,
            has_production_pallet_plan = hasProductionPalletPlan,
            needs_production_pallet_plan = needsProductionPalletPlan,
            planned_pallet_count = palletSummary.PlannedPalletCount,
            filled_pallet_count = palletSummary.FilledPalletCount,
            planned_qty = palletSummary.PlannedQty,
            filled_qty = palletSummary.FilledQty,
            pallet_plan_status = palletPlanStatus ?? string.Empty,
            pallet_fill_tone = palletFill.Tone,
            pallet_fill_label = palletFill.Label ?? string.Empty,
            pallet_fill_title = palletFill.Title ?? string.Empty,
            pallet_fill_show_completed_icon = palletFill.ShowCompletedIcon,
            production_pallet_plan_created = (hasProductionPalletPlan ?? false),
            production_pallet_plan_prepared = (hasProductionPalletPlan ?? false)
        };
    }
}
