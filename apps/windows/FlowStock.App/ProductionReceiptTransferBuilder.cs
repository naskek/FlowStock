using FlowStock.Core.Models;

namespace FlowStock.App;

public static class ProductionReceiptTransferBuilder
{
    private const double QtyTolerance = 0.000001d;

    public static IReadOnlyList<WpfAddDocLineContext> BuildInitialContexts(
        IReadOnlyList<OrderReceiptLine> receiptLines,
        long? defaultToLocationId)
    {
        return receiptLines
            .Where(line => line.QtyRemaining > QtyTolerance)
            .GroupBy(line => (line.OrderLineId, line.ItemId, line.ProductionPurpose))
            .OrderBy(group => group.Key.OrderLineId)
            .Select(group => new WpfAddDocLineContext(
                group.Key.ItemId,
                null,
                group.Key.OrderLineId,
                group.Key.ProductionPurpose,
                group.Sum(line => line.QtyRemaining),
                null,
                null,
                null,
                defaultToLocationId,
                null,
                null))
            .ToList();
    }
}
