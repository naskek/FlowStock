using FlowStock.Core.Models;

namespace FlowStock.App;

public static class OutboundOrderSelectionPolicy
{
    public static bool IsCandidate(OrderType orderType, OrderStatus status, bool hasShipmentRemaining)
    {
        return orderType == OrderType.Customer
               && status is OrderStatus.Accepted or OrderStatus.InProgress
               && hasShipmentRemaining;
    }
}
