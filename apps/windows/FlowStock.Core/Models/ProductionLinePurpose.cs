namespace FlowStock.Core.Models;

public enum ProductionLinePurpose
{
    CustomerOrder,
    InternalStock
}

public static class ProductionLinePurposeMapper
{
    public const string CustomerOrderValue = "CUSTOMER_ORDER";
    public const string InternalStockValue = "INTERNAL_STOCK";

    public static string ToDbValue(ProductionLinePurpose purpose)
    {
        return purpose == ProductionLinePurpose.CustomerOrder
            ? CustomerOrderValue
            : InternalStockValue;
    }

    public static ProductionLinePurpose FromDbValue(string? value, long? orderLineId = null)
    {
        if (string.Equals(value, CustomerOrderValue, StringComparison.OrdinalIgnoreCase))
        {
            return ProductionLinePurpose.CustomerOrder;
        }

        if (string.Equals(value, InternalStockValue, StringComparison.OrdinalIgnoreCase))
        {
            return ProductionLinePurpose.InternalStock;
        }

        return orderLineId.HasValue
            ? ProductionLinePurpose.CustomerOrder
            : ProductionLinePurpose.InternalStock;
    }

    public static string ToDisplayName(ProductionLinePurpose purpose)
    {
        return purpose == ProductionLinePurpose.CustomerOrder ? "Под заказ" : "На склад";
    }
}
