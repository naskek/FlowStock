using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedOrderListMetricsStore
{
    IReadOnlyDictionary<long, OrderListMetrics> GetOrderListMetrics(IReadOnlyCollection<long> orderIds);
}
