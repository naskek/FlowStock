using FlowStock.Core.Models;

namespace FlowStock.App;

public static class OrderReservationPromptPolicy
{
    private const double QtyTolerance = 0.000001;

    public static bool ShouldPrompt(
        IReadOnlyCollection<OrderLineView> lines,
        IReadOnlyCollection<HuStockContextRow> huStockRows,
        IReadOnlySet<long> reservationEnabledItemIds,
        long? orderId,
        out IReadOnlyList<string> freeHuCodes)
    {
        freeHuCodes = Array.Empty<string>();
        if (lines.Count == 0 || reservationEnabledItemIds.Count == 0)
        {
            return false;
        }

        var requiredByItem = lines
            .Where(line => reservationEnabledItemIds.Contains(line.ItemId))
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(GetRemainingQty));
        if (requiredByItem.Count == 0)
        {
            return false;
        }

        if (orderId.HasValue)
        {
            var ownReservedByItem = huStockRows
                .Where(row => row.ReservedCustomerOrderId == orderId.Value)
                .Where(row => row.Qty > QtyTolerance)
                .Where(row => reservationEnabledItemIds.Contains(row.ItemId))
                .GroupBy(row => row.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));

            foreach (var pair in ownReservedByItem)
            {
                if (requiredByItem.TryGetValue(pair.Key, out var required))
                {
                    requiredByItem[pair.Key] = Math.Max(0, required - pair.Value);
                }
            }
        }

        var shortageItemIds = requiredByItem
            .Where(pair => pair.Value > QtyTolerance)
            .Select(pair => pair.Key)
            .ToHashSet();
        if (shortageItemIds.Count == 0)
        {
            return false;
        }

        freeHuCodes = huStockRows
            .Where(row => shortageItemIds.Contains(row.ItemId))
            .Where(row => row.Qty > QtyTolerance)
            .Where(row => !string.IsNullOrWhiteSpace(row.Hu))
            .Where(row => row.OriginInternalOrderId.HasValue)
            .Where(row => !row.ReservedCustomerOrderId.HasValue)
            .Select(row => row.Hu.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return freeHuCodes.Count > 0;
    }

    private static double GetRemainingQty(OrderLineView line)
    {
        if (line.QtyShipped > QtyTolerance)
        {
            return Math.Max(0, line.QtyOrdered - line.QtyShipped);
        }

        if (line.QtyRemaining > QtyTolerance)
        {
            return line.QtyRemaining;
        }

        return Math.Max(0, line.QtyOrdered);
    }
}
