using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineQtyChangeRulesTests
{
    [Fact]
    public void Internal_DecreaseBelowFilled_IsBlocked()
    {
        var allowed = OrderLineQtyChangeRules.TryValidateQtyChange(
            newQty: 600,
            shippedQty: 0,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal,
            out var message);

        Assert.False(allowed);
        Assert.Contains("заполнено 1200", message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Internal_DecreaseToFilled_IsAllowed()
    {
        var allowed = OrderLineQtyChangeRules.TryValidateQtyChange(
            newQty: 1200,
            shippedQty: 0,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal,
            out var message);

        Assert.True(allowed);
        Assert.Null(message);
    }

    [Fact]
    public void Internal_PrintedSurplusDoesNotBlockDecrease()
    {
        var allowed = OrderLineQtyChangeRules.TryValidateQtyChange(
            newQty: 2400,
            shippedQty: 0,
            filledPalletQty: 1200,
            reservedPlanQty: 3600,
            OrderType.Internal,
            out var message);

        Assert.True(allowed);
        Assert.Null(message);
    }
}
