using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderLineQtyChangeRules
{
    private const double QtyTolerance = 0.000001d;

    public static bool TryValidateQtyChange(
        double newQty,
        double shippedQty,
        double filledPalletQty,
        double reservedPlanQty,
        OrderType orderType,
        out string? errorMessage)
    {
        var factualLockedQty = ResolveFactualLockedQty(shippedQty, filledPalletQty, reservedPlanQty, orderType);
        if (newQty + QtyTolerance < factualLockedQty)
        {
            errorMessage =
                $"Нельзя уменьшить количество ниже уже заполненного/выпущенного объема: заполнено {FormatLockedQty(factualLockedQty)}.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static double ResolveFactualLockedQty(
        double shippedQty,
        double filledPalletQty,
        double reservedPlanQty,
        OrderType orderType)
    {
        var shipped = Math.Max(0, shippedQty);
        var filled = Math.Max(0, filledPalletQty);
        if (orderType == OrderType.Customer)
        {
            var reserved = Math.Max(0, reservedPlanQty);
            return shipped + Math.Max(filled, reserved);
        }

        return shipped + filled;
    }

    public static string FormatLockedQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
