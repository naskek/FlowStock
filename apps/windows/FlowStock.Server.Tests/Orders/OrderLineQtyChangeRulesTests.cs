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
    public void Internal_PresentationFields_DoNotDoubleCountProducedAndFilledPalletQty()
    {
        var locked = OrderLineQtyChangeRules.ResolveFactualLockedQtyForPresentation(
            qtyShipped: 1200,
            qtyProduced: 1200,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal);

        Assert.Equal(1200, locked, 3);

        var allowed = OrderLineQtyChangeRules.TryValidateQtyChangeForPresentation(
            newQty: 600,
            qtyShipped: 1200,
            qtyProduced: 1200,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal,
            out var message);

        Assert.False(allowed);
        Assert.Contains("заполнено 1200", message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2400", message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Internal_ShippedAndFilledSameQty_DoesNotDoubleCount()
    {
        var locked = OrderLineQtyChangeRules.ResolveFactualLockedQty(
            shippedQty: 1200,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal);

        Assert.Equal(1200, locked, 3);

        var allowed = OrderLineQtyChangeRules.TryValidateQtyChange(
            newQty: 600,
            shippedQty: 1200,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal,
            out var message);

        Assert.False(allowed);
        Assert.Contains("заполнено 1200", message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2400", message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Internal_PlannedOnly_ResolvedLockedQty_IsZero()
    {
        var locked = OrderLineQtyChangeRules.ResolveFactualLockedQtyForPresentation(
            qtyShipped: 1200,
            qtyProduced: 0,
            filledPalletQty: 0,
            reservedPlanQty: 0,
            OrderType.Internal);

        Assert.Equal(0, locked, 3);

        var allowed = OrderLineQtyChangeRules.TryValidateQtyChangeForPresentation(
            newQty: 600,
            qtyShipped: 1200,
            qtyProduced: 0,
            filledPalletQty: 0,
            reservedPlanQty: 0,
            OrderType.Internal,
            out var message);

        Assert.True(allowed);
        Assert.Null(message);
    }

    [Fact]
    public void Internal_FilledQtyTakesPriority_OverZeroProduced()
    {
        var locked = OrderLineQtyChangeRules.ResolveFactualLockedQtyForPresentation(
            qtyShipped: 0,
            qtyProduced: 0,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal);

        Assert.Equal(1200, locked, 3);
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
