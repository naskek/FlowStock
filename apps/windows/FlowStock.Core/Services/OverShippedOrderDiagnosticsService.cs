using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OverShippedOrderDiagnosticsService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<OverShippedOrderDiagnosticItem> GetItems()
    {
        if (_dataStore is IOverShippedOrderDiagnosticsStore diagnosticsStore)
        {
            return diagnosticsStore.GetOverShippedOrderDiagnostics();
        }

        return Array.Empty<OverShippedOrderDiagnosticItem>();
    }
}
