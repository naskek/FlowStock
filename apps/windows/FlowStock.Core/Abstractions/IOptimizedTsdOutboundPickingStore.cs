using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOptimizedTsdOutboundPickingStore
{
    IReadOnlyList<OutboundPickingOrderRow> GetTsdOutboundOrderRows();
}
