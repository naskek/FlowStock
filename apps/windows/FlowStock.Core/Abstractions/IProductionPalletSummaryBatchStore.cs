using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IProductionPalletSummaryBatchStore
{
    IReadOnlyDictionary<long, ProductionPalletSummary> GetProductionPalletSummariesByDocIds(IReadOnlyCollection<long> docIds);
}
