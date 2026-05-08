using FlowStock.Core.Models;

namespace FlowStock.App;

public static class OrderMarkingExportUiPolicy
{
    public static bool CanExport(Order? order, IReadOnlyCollection<OrderLineView> lines)
    {
        return order?.Status is not OrderStatus.Cancelled and not OrderStatus.Shipped
               && lines.Any(line => !string.IsNullOrWhiteSpace(line.Gtin));
    }
}
