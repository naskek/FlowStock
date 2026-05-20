using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOverShippedOrderDiagnosticsStore
{
    IReadOnlyList<OverShippedOrderDiagnosticItem> GetOverShippedOrderDiagnostics();
}
