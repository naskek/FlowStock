using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineEditPrevalidationTests
{
    [Fact]
    public void A_PlannedHuOnly_AllowsDecrease()
    {
        var line = BuildInternalLine(qtyOrdered: 1200, qtyProduced: 0, filledPalletQty: 0, huCodes: "HU-1, HU-2");

        var allowed = OrderLineEditPrevalidation.TryValidateQtyChange(600, line, OrderType.Internal, out var message);

        Assert.True(allowed);
        Assert.Null(message);
        Assert.False(OrderLineEditPrevalidation.ShouldBlockLocalQtyApply(allowed));
    }

    [Fact]
    public void B_FilledBlocks_WithLockedQty1200_Not2400()
    {
        var allowed = OrderLineEditPrevalidation.TryValidateQtyChange(
            600,
            qtyShipped: 0,
            qtyProduced: 1200,
            filledPalletQty: 1200,
            OrderType.Internal,
            out var message);

        Assert.False(allowed);
        Assert.Contains("заполнено 1200", message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2400", message ?? string.Empty, StringComparison.Ordinal);
        Assert.True(OrderLineEditPrevalidation.ShouldBlockLocalQtyApply(allowed));
    }

    [Fact]
    public void C_LegacyQtyShipped_DoesNotDoubleCount_WhenProducedAndFilledMatch()
    {
        var locked = OrderLineQtyChangeRules.ResolveFactualLockedQtyForPresentation(
            qtyShipped: 1200,
            qtyProduced: 1200,
            filledPalletQty: 1200,
            reservedPlanQty: 0,
            OrderType.Internal);

        Assert.Equal(1200, locked, 3);
    }

    [Fact]
    public void D_StaleQtyShipped_WithPlannedOnly_DoesNotBlock()
    {
        var line = BuildInternalLine(
            qtyOrdered: 1200,
            qtyProduced: 0,
            filledPalletQty: 0,
            qtyShipped: 1200,
            huCodes: "HU-1, HU-2");

        var locked = OrderLineQtyChangeRules.ResolveFactualLockedQtyForPresentation(
            line.QtyShipped,
            line.QtyProduced,
            line.FilledPalletQty,
            0,
            OrderType.Internal);

        Assert.Equal(0, locked, 3);

        var allowed = OrderLineEditPrevalidation.TryValidateQtyChange(600, line, OrderType.Internal, out var message);

        Assert.True(allowed);
        Assert.Null(message);
        Assert.False(OrderLineEditPrevalidation.ShouldBlockLocalQtyApply(allowed));
    }

    [Fact]
    public void E_FailedPersist_ShouldReloadMetrics_FromServer()
    {
        Assert.True(OrderLineEditPrevalidation.ShouldReloadLineMetricsAfterFailedPersist(false));
        Assert.False(OrderLineEditPrevalidation.ShouldReloadLineMetricsAfterFailedPersist(true));
    }

    [Fact]
    public void F_SuccessfulPersist_ShouldReloadCanonicalOrder()
    {
        Assert.True(OrderLineEditPrevalidation.ShouldReloadCanonicalOrderAfterSuccessfulPersist(true));
        Assert.False(OrderLineEditPrevalidation.ShouldReloadCanonicalOrderAfterSuccessfulPersist(false));
    }

    [Fact]
    public void UiGate_AllowedValidation_DoesNotShowWarning()
    {
        Assert.False(OrderLineEditPrevalidation.ShouldBlockLocalQtyApply(validationAllowed: true));
    }

    [Fact]
    public void UiGate_RejectedValidation_ShowsWarning()
    {
        Assert.True(OrderLineEditPrevalidation.ShouldBlockLocalQtyApply(validationAllowed: false));
    }

    private static OrderLineView BuildInternalLine(
        double qtyOrdered,
        double qtyProduced,
        double filledPalletQty,
        string huCodes = "",
        double qtyShipped = 0)
    {
        return new OrderLineView
        {
            Id = 1,
            OrderId = 80,
            ItemId = 6,
            ItemName = "Smoke item",
            QtyOrdered = qtyOrdered,
            QtyProduced = qtyProduced,
            FilledPalletQty = filledPalletQty,
            QtyShipped = qtyShipped,
            ProductionHuCodes = huCodes,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            PlannedPalletCount = string.IsNullOrWhiteSpace(huCodes) ? 0 : 2
        };
    }
}
