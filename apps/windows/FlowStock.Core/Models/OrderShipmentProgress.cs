namespace FlowStock.Core.Models;

public sealed class OrderShipmentProgress
{
    public double OrderedQty { get; init; }
    public double ShippedQty { get; init; }
    public double RemainingQty { get; init; }
    public bool IsPartiallyShipped =>
        ShippedQty > StockQuantityRules.QtyTolerance
        && RemainingQty > StockQuantityRules.QtyTolerance;
}
