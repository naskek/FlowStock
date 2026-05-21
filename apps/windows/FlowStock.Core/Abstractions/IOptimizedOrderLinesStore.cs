using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedOrderLinesStore
{
    IReadOnlyDictionary<long, IReadOnlyList<OrderLineView>> GetOrderLineViewsByOrderIds(IReadOnlyCollection<long> orderIds);

    IReadOnlyDictionary<long, string[]> GetProductionHuCodesByOrderLineIds(IReadOnlyCollection<long> orderLineIds);
}
