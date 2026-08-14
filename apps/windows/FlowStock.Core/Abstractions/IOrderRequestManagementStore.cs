using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOrderRequestManagementStore
{
    OrderRequest? GetOrderRequestForUpdate(long requestId);

    bool TryResolvePendingOrderRequest(
        long requestId,
        string status,
        DateTime resolvedAt,
        string resolvedBy,
        string? note,
        long? appliedOrderId);
}
