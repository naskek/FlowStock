using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderLineQtyChangeRules
{
    private const double QtyTolerance = 0.000001d;

    public static double ResolveFactualLockedQtyForPresentation(
        double qtyShipped,
        double qtyProduced,
        double filledPalletQty,
        double reservedPlanQty,
        OrderType orderType)
    {
        if (orderType == OrderType.Internal)
        {
            // Для INTERNAL API кладёт qty_produced в поле qty_shipped; не складывать с pallet_filled_qty.
            var produced = Math.Max(0, qtyProduced);
            var filled = Math.Max(0, filledPalletQty);
            return Math.Max(produced, filled);
        }

        return ResolveFactualLockedQty(qtyShipped, filledPalletQty, reservedPlanQty, orderType);
    }

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

    public static bool TryValidateQtyChangeForPresentation(
        double newQty,
        double qtyShipped,
        double qtyProduced,
        double filledPalletQty,
        double reservedPlanQty,
        OrderType orderType,
        out string? errorMessage)
    {
        var factualLockedQty = ResolveFactualLockedQtyForPresentation(
            qtyShipped,
            qtyProduced,
            filledPalletQty,
            reservedPlanQty,
            orderType);
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

        // INTERNAL: outbound shipped и pallet filled описывают один фактический выпуск — не суммировать.
        return Math.Max(shipped, filled);
    }

    public static string FormatLockedQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
