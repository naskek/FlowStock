using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class ReservedFilledHuMarkingCalculator
{
    private const double QtyTolerance = 0.000001d;

    public static IReadOnlyDictionary<long, double> GetQtyByOrderLine(IDataStore store, long customerOrderId)
    {
        var order = store.GetOrder(customerOrderId);
        if (order?.Type != OrderType.Customer)
        {
            return new Dictionary<long, double>();
        }

        var stockByItemHu = store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => new { row.ItemId, Hu = NormalizeHu(row.HuCode), row.Qty })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Hu))
            .GroupBy(entry => (entry.ItemId, Hu: entry.Hu!))
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Qty), ItemHuComparer.Instance);

        var totals = new Dictionary<long, double>();
        foreach (var planLine in store.GetOrderReceiptPlanLines(customerOrderId))
        {
            if (planLine.QtyPlanned <= QtyTolerance)
            {
                continue;
            }

            var huCode = NormalizeHu(planLine.ToHu);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            var pallet = store.GetProductionPalletByHu(huCode);
            if (pallet == null
                || pallet.ItemId != planLine.ItemId
                || !string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!stockByItemHu.TryGetValue((planLine.ItemId, huCode), out var stockQty) || stockQty <= QtyTolerance)
            {
                continue;
            }

            var coveredQty = Math.Min(planLine.QtyPlanned, Math.Min(stockQty, pallet.PlannedQty));
            if (coveredQty <= QtyTolerance)
            {
                continue;
            }

            totals.TryGetValue(planLine.OrderLineId, out var current);
            totals[planLine.OrderLineId] = current + coveredQty;
        }

        return totals;
    }

    private static string? NormalizeHu(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant();
    }

    private sealed class ItemHuComparer : IEqualityComparer<(long ItemId, string Hu)>
    {
        public static readonly ItemHuComparer Instance = new();

        public bool Equals((long ItemId, string Hu) x, (long ItemId, string Hu) y)
        {
            return x.ItemId == y.ItemId
                   && string.Equals(x.Hu, y.Hu, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((long ItemId, string Hu) obj)
        {
            return HashCode.Combine(obj.ItemId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hu));
        }
    }
}
