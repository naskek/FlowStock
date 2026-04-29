namespace FlowStock.Core.Services;

public static class MinStockControlCalculator
{
    public static double CalculateAvailableForMinStock(
        double physicalLedgerStockQty,
        double reservedCustomerOrderQty,
        bool minStockUsesOrderBinding)
    {
        if (!minStockUsesOrderBinding)
        {
            return physicalLedgerStockQty;
        }

        var safeReservedQty = reservedCustomerOrderQty > 0 ? reservedCustomerOrderQty : 0;
        return physicalLedgerStockQty - safeReservedQty;
    }
}
