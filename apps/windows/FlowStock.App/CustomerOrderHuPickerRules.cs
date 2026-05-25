using FlowStock.Core.Models;

namespace FlowStock.App;

public static class CustomerOrderHuPickerRules
{
    public const double QtyTolerance = 0.000001;

    public static double ComputeManualBindingCapacity(OrderLineView line)
    {
        var ordered = Math.Max(0, line.QtyOrdered);
        var shipped = Math.Max(0, line.QtyShipped);
        var palletCovered = Math.Max(0, line.PlannedPalletQty);
        return Math.Max(0, ordered - shipped - palletCovered);
    }

    public static double ComputeManualBindableRemaining(OrderLineView line, double boundHuQty) =>
        Math.Max(0, ComputeManualBindingCapacity(line) - Math.Max(0, boundHuQty));

    public static bool IsFullyCoveredByProductionPalletPlan(OrderLineView line, double boundHuQty) =>
        line.QtyOrdered > QtyTolerance
        && line.PlannedPalletQty > QtyTolerance
        && ComputeManualBindableRemaining(line, boundHuQty) <= QtyTolerance;

    public static bool IsPartiallyCoveredByProductionPalletPlan(OrderLineView line, double boundHuQty) =>
        line.PlannedPalletQty > QtyTolerance
        && ComputeManualBindableRemaining(line, boundHuQty) > QtyTolerance;

    public static string BuildHuPickerLabel(
        bool hasOrderId,
        OrderLineView line,
        double boundHuQty,
        int selectedHuCount,
        bool awaitingSave,
        bool candidatesFailed)
    {
        if (!hasOrderId)
        {
            return "После сохранения";
        }

        if (candidatesFailed)
        {
            return "HU…";
        }

        if (selectedHuCount > 0)
        {
            return $"HU ({selectedHuCount})";
        }

        if (IsFullyCoveredByProductionPalletPlan(line, boundHuQty))
        {
            return "Покрыто планом";
        }

        if (IsPartiallyCoveredByProductionPalletPlan(line, boundHuQty))
        {
            return $"Выбрать HU ({FormatQty(ComputeManualBindableRemaining(line, boundHuQty))})";
        }

        return "Выбрать HU";
    }

    public static string? BuildHuPickerToolTip(
        OrderLineView line,
        double boundHuQty,
        bool awaitingSave,
        bool candidatesFailed,
        bool isPickerEnabled)
    {
        if (awaitingSave)
        {
            return "Сохраните заказ, чтобы привязать HU.";
        }

        var manualRemaining = ComputeManualBindableRemaining(line, boundHuQty);
        var palletHuCodes = line.ProductionHuCodes?.Trim() ?? string.Empty;

        if (line.PlannedPalletQty > QtyTolerance && manualRemaining <= QtyTolerance && boundHuQty <= QtyTolerance)
        {
            return string.IsNullOrWhiteSpace(palletHuCodes)
                ? "Строка покрыта паллетным планом."
                : $"Строка покрыта паллетным планом: {palletHuCodes}";
        }

        if (IsPartiallyCoveredByProductionPalletPlan(line, boundHuQty))
        {
            return "Часть строки покрыта паллетным планом, выбрать HU можно только на остаток.";
        }

        if (!isPickerEnabled && manualRemaining <= QtyTolerance && boundHuQty > QtyTolerance)
        {
            return "Строка покрыта выбранными HU.";
        }

        if (!isPickerEnabled && manualRemaining <= QtyTolerance)
        {
            return "Остатка для ручной привязки HU нет.";
        }

        return null;
    }

    public static bool IsHuPickerEnabled(
        bool hasOrderId,
        OrderLineView line,
        double boundHuQty,
        bool awaitingSave)
    {
        if (!hasOrderId
            || awaitingSave
            || line.ItemId <= 0
            || line.QtyOrdered <= QtyTolerance)
        {
            return false;
        }

        if (IsFullyCoveredByProductionPalletPlan(line, boundHuQty) && boundHuQty <= QtyTolerance)
        {
            return false;
        }

        var manualRemaining = ComputeManualBindableRemaining(line, boundHuQty);
        return manualRemaining > QtyTolerance || boundHuQty > QtyTolerance;
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> BuildExcludeHuCodesForOtherLines(
        IEnumerable<CustomerOrderLineHuState> states,
        string clientLineKey)
    {
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (string.Equals(state.ClientLineKey, clientLineKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var huCode in state.SelectedHuCodes)
            {
                exclude.Add(huCode);
            }
        }

        return exclude.ToArray();
    }

    public static double SumSelectedQty(IEnumerable<HuReservationPickerRow> rows) =>
        rows.Where(row => row.IsSelected).Sum(row => row.Qty);

    public static void ApplyRowEnablement(
        IReadOnlyList<HuReservationPickerRow> rows,
        double lineRemainingQty,
        IReadOnlySet<string> selectedOnOtherLines)
    {
        var selectedQty = SumSelectedQty(rows);
        var remainingCapacity = Math.Max(0, lineRemainingQty - selectedQty);

        foreach (var row in rows)
        {
            if (row.IsSelected)
            {
                row.SetEnablement(true, null);
                continue;
            }

            if (selectedOnOtherLines.Contains(row.HuCode))
            {
                row.SetEnablement(false, "Выбран в другой строке заказа");
                continue;
            }

            if (remainingCapacity <= QtyTolerance)
            {
                row.SetEnablement(false, "Покрыто выбранными HU");
                continue;
            }

            if (row.Qty > remainingCapacity + QtyTolerance)
            {
                row.SetEnablement(false, "Превышает количество строки");
                continue;
            }

            row.SetEnablement(true, null);
        }
    }

    public static bool TrySelectRow(
        HuReservationPickerRow row,
        IReadOnlyList<HuReservationPickerRow> allRows,
        double lineRemainingQty,
        bool desiredSelected)
    {
        if (!desiredSelected)
        {
            return true;
        }

        if (!row.IsEnabled)
        {
            return false;
        }

        var selectedQty = SumSelectedQty(allRows);
        if (selectedQty + row.Qty > lineRemainingQty + QtyTolerance)
        {
            return false;
        }

        return true;
    }
}
