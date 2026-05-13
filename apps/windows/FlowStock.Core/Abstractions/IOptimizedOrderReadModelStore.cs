using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedOrderReadModelStore
{
    IReadOnlyList<ProductionNeedRow> GetProductionNeedRows(bool includeZeroNeed);
    IReadOnlyDictionary<long, double> GetUnlinkedProductionTotalsByItem(long orderId);
}
