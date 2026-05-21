namespace FlowStock.App;

public static class CustomerOrderHuPickerRules
{
    public const double QtyTolerance = 0.000001;

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
