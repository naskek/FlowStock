using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Catalog;

public sealed class MinStockControlCalculatorTests
{
    [Fact]
    public void AvailableForMinStock_WhenFlagDisabled_UsesPhysicalLedgerStock()
    {
        var available = MinStockControlCalculator.CalculateAvailableForMinStock(
            physicalLedgerStockQty: 4800,
            reservedCustomerOrderQty: 4200,
            minStockUsesOrderBinding: false);

        var shortage = Math.Max(0, 1800 - available);
        Assert.Equal(4800, available);
        Assert.Equal(0, shortage);
    }

    [Fact]
    public void AvailableForMinStock_WhenFlagEnabled_SubtractsActiveReservation()
    {
        var available = MinStockControlCalculator.CalculateAvailableForMinStock(
            physicalLedgerStockQty: 4800,
            reservedCustomerOrderQty: 4200,
            minStockUsesOrderBinding: true);

        var shortage = Math.Max(0, 1800 - available);
        Assert.Equal(600, available);
        Assert.Equal(1200, shortage);
    }

    [Fact]
    public void AvailableForMinStock_AfterShipment_DoesNotDoubleSubtractReserve()
    {
        var available = MinStockControlCalculator.CalculateAvailableForMinStock(
            physicalLedgerStockQty: 600,
            reservedCustomerOrderQty: 0,
            minStockUsesOrderBinding: true);

        Assert.Equal(600, available);
    }

    [Fact]
    public void AvailableForMinStock_IgnoresInactiveOrderReserve_WhenReservedQtyIsZero()
    {
        var available = MinStockControlCalculator.CalculateAvailableForMinStock(
            physicalLedgerStockQty: 1200,
            reservedCustomerOrderQty: 0,
            minStockUsesOrderBinding: true);

        Assert.Equal(1200, available);
    }

    [Fact]
    public void AvailableForMinStock_OtherTypesWithoutFlag_KeepLegacyBehavior()
    {
        var mustardAvailable = MinStockControlCalculator.CalculateAvailableForMinStock(
            physicalLedgerStockQty: 4800,
            reservedCustomerOrderQty: 4200,
            minStockUsesOrderBinding: false);
        var ketchupAvailable = MinStockControlCalculator.CalculateAvailableForMinStock(
            physicalLedgerStockQty: 3500,
            reservedCustomerOrderQty: 1400,
            minStockUsesOrderBinding: false);

        Assert.Equal(4800, mustardAvailable);
        Assert.Equal(3500, ketchupAvailable);
    }
}
