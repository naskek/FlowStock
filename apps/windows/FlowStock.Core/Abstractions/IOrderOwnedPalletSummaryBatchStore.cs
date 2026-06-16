using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOrderOwnedPalletSummaryBatchStore
{
    IReadOnlyDictionary<long, ProductionPalletSummary> GetOrderOwnedProductionPalletSummaries(IReadOnlyCollection<long> orderIds);
}
