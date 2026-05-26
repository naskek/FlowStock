using FlowStock.Core.Models;

namespace FlowStock.App;

public static class ProductionPalletGroupEditability
{
    public static void Apply(
        IEnumerable<OrderLineView> lines,
        IReadOnlySet<long> blockedOrderLineIds,
        bool orderEditable)
    {
        foreach (var line in lines)
        {
            line.IsProductionPalletGroupEditable = orderEditable
                                                  && line.Id > 0
                                                  && !blockedOrderLineIds.Contains(line.Id);
        }
    }
}
