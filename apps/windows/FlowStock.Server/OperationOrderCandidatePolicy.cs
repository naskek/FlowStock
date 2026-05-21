using FlowStock.Core.Models;

namespace FlowStock.Server;

public static class OperationOrderCandidatePolicy
{
    public static bool IsCandidate(Order order, DocType docType)
    {
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            return false;
        }

        return docType switch
        {
            DocType.ProductionReceipt => order.NeedsProductionPalletPlan,
            DocType.Outbound => order.Type == OrderType.Customer && order.HasShipmentRemaining,
            _ => false
        };
    }
}
