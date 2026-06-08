using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderShipmentProgressService
{
    public static OrderShipmentProgress Get(IDataStore store, long orderId)
    {
        var lines = store.GetOrderLines(orderId);
        var shippedByLine = store.GetShippedTotalsByOrderLine(orderId);

        return new OrderShipmentProgress
        {
            OrderedQty = lines.Sum(line => Math.Max(0, line.QtyOrdered)),
            ShippedQty = lines.Sum(line =>
                shippedByLine.TryGetValue(line.Id, out var shipped) ? Math.Max(0, shipped) : 0d),
            RemainingQty = lines.Sum(line =>
            {
                var shipped = shippedByLine.TryGetValue(line.Id, out var value) ? Math.Max(0, value) : 0d;
                return Math.Max(0, line.QtyOrdered - shipped);
            })
        };
    }
}
