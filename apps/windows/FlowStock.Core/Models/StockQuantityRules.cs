namespace FlowStock.Core.Models;

public static class StockQuantityRules
{
    public const double QtyTolerance = 0.000001d;

    public static bool IsEffectivelyZero(double qty) => Math.Abs(qty) <= QtyTolerance;

    public static bool IsActiveStockQty(double qty) => qty > QtyTolerance;

    public static bool IsNegativeStockQty(double qty) => qty < -QtyTolerance;
}
