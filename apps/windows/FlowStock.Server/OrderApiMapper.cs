using System.Globalization;
using FlowStock.Core.Models;

namespace FlowStock.Server;

public static class OrderApiMapper
{
    public static object MapOrder(Order order)
    {
        var markingStatus = order.EffectiveMarkingStatus;

        return new
        {
            id = order.Id,
            order_ref = order.OrderRef,
            order_type = OrderStatusMapper.TypeToString(order.Type),
            partner_id = order.PartnerId,
            partner_name = order.PartnerName,
            partner_code = order.PartnerCode,
            due_date = order.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            status = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            comment = order.Comment,
            bind_reserved_stock = order.UseReservedStock,
            marking_status = MarkingStatusMapper.ToString(markingStatus),
            marking_required = order.MarkingRequired,
            marking_effective_status = MarkingStatusMapper.ToString(markingStatus),
            marking_status_display = order.MarkingStatusDisplay,
            marking_status_label = order.MarkingStatusDisplay,
            marking_excel_generated_at = order.MarkingExcelGeneratedAt?.ToString("O", CultureInfo.InvariantCulture),
            marking_printed_at = order.MarkingPrintedAt?.ToString("O", CultureInfo.InvariantCulture),
            created_at = order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            shipped_at = order.ShippedAt?.ToString("O", CultureInfo.InvariantCulture)
        };
    }
}
