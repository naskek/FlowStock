using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderLinePalletFillPresentation
{
    public string FulfillmentStatus { get; init; } = string.Empty;
    public bool LineFullyShipped { get; init; }
    public bool HidePalletFillIndicator { get; init; }
    public bool BlockingFillRequired { get; init; }
    public string? Label { get; init; }
    public string Tone { get; init; } = "neutral";
    public string? Title { get; init; }
}

public static class OrderLinePalletFillPresentationService
{
    public static bool IsLineFullyShipped(OrderType orderType, double qtyOrdered, double qtyShipped)
    {
        return orderType == OrderType.Customer
               && qtyOrdered > StockQuantityRules.QtyTolerance
               && qtyShipped + StockQuantityRules.QtyTolerance >= qtyOrdered;
    }

    public static double GetRemainingToShip(double qtyOrdered, double qtyShipped)
    {
        return Math.Max(0, qtyOrdered - qtyShipped);
    }

    public static OrderLinePalletFillPresentation Resolve(Order order, OrderLineView line)
    {
        if (IsLineFullyShipped(order.Type, line.QtyOrdered, line.QtyShipped))
        {
            return new OrderLinePalletFillPresentation
            {
                FulfillmentStatus = "SHIPPED",
                LineFullyShipped = true,
                HidePalletFillIndicator = true,
                BlockingFillRequired = false,
                Tone = "completed",
                Label = null,
                Title = "Строка полностью отгружена"
            };
        }

        if (order.Type == OrderType.Internal)
        {
            return ResolveInternal(line);
        }

        return ResolveCustomerFillProgress(line);
    }

    public static void Apply(Order order, OrderLineView line)
    {
        var presentation = Resolve(order, line);
        line.LineFullyShipped = presentation.LineFullyShipped;
        line.HidePalletFillIndicator = presentation.HidePalletFillIndicator;
        line.BlockingFillRequired = presentation.BlockingFillRequired;
        line.PalletFillLabel = presentation.Label;
        line.PalletFillTone = presentation.Tone;
        line.PalletFillTitle = presentation.Title;
        line.FulfillmentStatus = presentation.FulfillmentStatus;
    }

    private static OrderLinePalletFillPresentation ResolveInternal(OrderLineView line)
    {
        var hasPlan = line.PlannedPalletCount > 0 || line.PlannedPalletQty > StockQuantityRules.QtyTolerance;
        if (!hasPlan)
        {
            return new OrderLinePalletFillPresentation
            {
                FulfillmentStatus = "NO_PLAN",
                HidePalletFillIndicator = true,
                BlockingFillRequired = false
            };
        }

        var complete = line.PlannedPalletQty <= StockQuantityRules.QtyTolerance
                         || line.FilledPalletQty + StockQuantityRules.QtyTolerance >= line.PlannedPalletQty;
        if (line.PlannedPalletCount > 0)
        {
            complete = complete && line.FilledPalletCount >= line.PlannedPalletCount;
        }

        if (!complete && (line.FilledPalletCount > 0 || line.FilledPalletQty > StockQuantityRules.QtyTolerance))
        {
            var label = line.PlannedPalletCount > 0
                ? $"Наполнено {line.FilledPalletCount} / {line.PlannedPalletCount}"
                : $"Наполнено {FormatQty(line.FilledPalletQty)}";
            var title = line.PlannedPalletCount > 0
                ? $"Наполнение по строке: {line.FilledPalletCount} / {line.PlannedPalletCount} паллет"
                : $"Наполнение по строке: {FormatQty(line.FilledPalletQty)} / {FormatQty(line.PlannedPalletQty)}";
            return new OrderLinePalletFillPresentation
            {
                FulfillmentStatus = "IN_FILL",
                BlockingFillRequired = true,
                Label = label,
                Tone = "ready",
                Title = title
            };
        }

        if (complete)
        {
            return new OrderLinePalletFillPresentation
            {
                FulfillmentStatus = "FILLED",
                HidePalletFillIndicator = true,
                BlockingFillRequired = false,
                Tone = "completed",
                Title = "Наполнение по строке завершено"
            };
        }

        var planLabel = line.PlannedPalletCount > 0
            ? $"План {line.PlannedPalletCount}"
            : $"План {FormatQty(line.PlannedPalletQty)}";
        return new OrderLinePalletFillPresentation
        {
            FulfillmentStatus = "PLANNED",
            BlockingFillRequired = false,
            Label = planLabel,
            Tone = "neutral",
            Title = "По строке есть паллетный план, наполнения пока нет"
        };
    }

    private static OrderLinePalletFillPresentation ResolveCustomerFillProgress(OrderLineView line)
    {
        var remainingToShip = GetRemainingToShip(line.QtyOrdered, line.QtyShipped);
        var hasPlan = line.PlannedPalletCount > 0 || line.PlannedPalletQty > StockQuantityRules.QtyTolerance;
        if (!hasPlan || remainingToShip <= StockQuantityRules.QtyTolerance)
        {
            return new OrderLinePalletFillPresentation
            {
                FulfillmentStatus = remainingToShip <= StockQuantityRules.QtyTolerance ? "SHIPPED" : "OPEN",
                HidePalletFillIndicator = true,
                BlockingFillRequired = false
            };
        }

        var hasFilled = line.FilledPalletCount > 0 || line.FilledPalletQty > StockQuantityRules.QtyTolerance;
        if (!hasFilled)
        {
            var planLabel = line.PlannedPalletCount > 0
                ? $"План {line.PlannedPalletCount}"
                : $"План {FormatQty(line.PlannedPalletQty)}";
            return new OrderLinePalletFillPresentation
            {
                FulfillmentStatus = "PLANNED",
                BlockingFillRequired = false,
                Label = planLabel,
                Tone = "neutral",
                Title = $"Паллетный план по строке; к отгрузке осталось {FormatQty(remainingToShip)}"
            };
        }

        var label = line.PlannedPalletCount > 0
            ? $"Наполнено {line.FilledPalletCount} / {line.PlannedPalletCount}"
            : $"Наполнено {FormatQty(line.FilledPalletQty)}";
        var title = line.PlannedPalletCount > 0
            ? $"Наполнение по строке: {line.FilledPalletCount} / {line.PlannedPalletCount} паллет. К отгрузке: {FormatQty(remainingToShip)}"
            : $"Наполнение по строке: {FormatQty(line.FilledPalletQty)} / {FormatQty(line.PlannedPalletQty)}. К отгрузке: {FormatQty(remainingToShip)}";

        return new OrderLinePalletFillPresentation
        {
            FulfillmentStatus = "IN_FILL",
            BlockingFillRequired = false,
            Label = label,
            Tone = "ready",
            Title = title
        };
    }

    private static string FormatQty(double qty) => qty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public static class OrderPalletFillPresentationService
{
    public static bool IsCustomerOrderFullyShipped(Order order)
    {
        return order.Type == OrderType.Customer
               && (order.Status == OrderStatus.Shipped || !order.HasShipmentRemaining);
    }

    public static string? ResolveOrderPalletPlanStatus(
        Order order,
        bool needsProductionPalletPlan,
        bool hasProductionPalletPlan,
        ProductionPalletSummary summary)
    {
        if (IsCustomerOrderFullyShipped(order))
        {
            return string.Empty;
        }

        if (needsProductionPalletPlan && !hasProductionPalletPlan)
        {
            return "План не сформирован";
        }

        if (!hasProductionPalletPlan || summary.PlannedPalletCount <= 0)
        {
            return string.Empty;
        }

        if (summary.FilledPalletCount >= summary.PlannedPalletCount && summary.RemainingPalletCount <= 0)
        {
            return $"Наполнено полностью: {summary.FilledPalletCount} / {summary.PlannedPalletCount} паллет";
        }

        if (summary.FilledPalletCount > 0 || summary.FilledQty > 0)
        {
            return $"Наполнение идёт: {summary.FilledPalletCount} / {summary.PlannedPalletCount}";
        }

        return "План сформирован";
    }

    public static bool HasStaleUnfilledPlanAfterShipment(Order order, ProductionPalletSummary summary)
    {
        return IsCustomerOrderFullyShipped(order)
               && summary.PlannedPalletCount > 0
               && summary.FilledPalletCount < summary.PlannedPalletCount;
    }
}
