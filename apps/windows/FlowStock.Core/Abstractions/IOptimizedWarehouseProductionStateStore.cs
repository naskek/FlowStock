using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedWarehouseProductionStateStore
{
    IReadOnlyDictionary<long, IReadOnlyList<WarehouseProductionStateCustomerOrderRow>> GetWarehouseProductionStateCustomerOrdersByItem();
    IReadOnlyDictionary<long, IReadOnlyList<WarehouseProductionStateInternalOrderRow>> GetWarehouseProductionStateInternalOrdersByItem();
    IReadOnlyDictionary<long, WarehouseProductionStatePalletAggregate> GetWarehouseProductionStatePalletsByItem();
}
