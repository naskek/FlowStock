using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IProductionPlanConsistencyDiagnosticsStore
{
    IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> GetProductionPlanConsistencyDiagnostics();
}
