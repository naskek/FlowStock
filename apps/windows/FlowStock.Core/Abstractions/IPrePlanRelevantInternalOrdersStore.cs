using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Read-only pre-plan scope: only INTERNAL orders with matching order lines or
/// matching production-pallet components may affect CUSTOMER coverage preview.
/// </summary>
public interface IPrePlanRelevantInternalOrdersStore
{
    IReadOnlyList<Order> GetPrePlanRelevantInternalOrders(IReadOnlyCollection<long> itemIds);
}
