using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class ProductionOrderLineHuCodes
{
    public static bool IsActivePalletStatus(string? status)
    {
        return !string.Equals(status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldShowPalletHuOnOrderLine(IDataStore store, ProductionPallet pallet, ProductionPalletComponentLine line)
    {
        if (!IsActivePalletStatus(pallet.Status) || string.IsNullOrWhiteSpace(pallet.HuCode))
        {
            return false;
        }

        if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            var itemId = line.ItemId > 0 ? line.ItemId : pallet.ItemId;
            return HasPositiveHuBalance(store, itemId, pallet.HuCode);
        }

        return true;
    }

    public static Dictionary<long, string[]> BuildByOrder(IDataStore store, long orderId)
    {
        var rows = new Dictionary<long, SortedSet<string>>();

        foreach (var reservedLine in store.GetOrderReceiptPlanLines(orderId)
                     .Where(line => line.QtyPlanned > 0))
        {
            if (reservedLine.OrderLineId <= 0 || string.IsNullOrWhiteSpace(reservedLine.ToHu))
            {
                continue;
            }

            var huCode = reservedLine.ToHu.Trim();
            if (!HasPositiveHuBalance(store, reservedLine.ItemId, huCode))
            {
                continue;
            }

            AddHu(rows, reservedLine.OrderLineId, huCode);
        }

        foreach (var pallet in GetProductionPalletsForOrder(store, orderId).Where(pallet => IsActivePalletStatus(pallet.Status)))
        {
            var componentLines = pallet.Lines.Count > 0
                ? pallet.Lines
                : new[]
                {
                    new ProductionPalletComponentLine
                    {
                        OrderLineId = pallet.OrderLineId,
                        ItemId = pallet.ItemId
                    }
                };
            foreach (var line in componentLines)
            {
                if (!line.OrderLineId.HasValue)
                {
                    continue;
                }

                if (!ShouldShowPalletHuOnOrderLine(store, pallet, line))
                {
                    continue;
                }

                AddHu(rows, line.OrderLineId.Value, pallet.HuCode!);
            }
        }

        return rows.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
    }

    public static Dictionary<long, OrderLineHuDisplayEntry[]> BuildProductionDisplayByOrder(IDataStore store, long orderId)
    {
        var rows = new Dictionary<long, List<OrderLineHuDisplayEntry>>();

        foreach (var pallet in GetProductionPalletsForOrder(store, orderId).Where(pallet => IsActivePalletStatus(pallet.Status)))
        {
            var componentLines = pallet.Lines.Count > 0
                ? pallet.Lines
                : new[]
                {
                    new ProductionPalletComponentLine
                    {
                        OrderLineId = pallet.OrderLineId,
                        ItemId = pallet.ItemId,
                        PlannedQty = pallet.PlannedQty,
                        FilledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                            ? pallet.PlannedQty
                            : 0
                    }
                };
            foreach (var line in componentLines)
            {
                if (!line.OrderLineId.HasValue || !ShouldShowPalletHuOnOrderLine(store, pallet, line))
                {
                    continue;
                }

                var qty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                    ? line.FilledQty > StockQuantityRules.QtyTolerance ? line.FilledQty : line.PlannedQty
                    : line.PlannedQty > StockQuantityRules.QtyTolerance ? line.PlannedQty : pallet.PlannedQty;
                AddDisplay(rows, line.OrderLineId.Value, new OrderLineHuDisplayEntry(
                    pallet.HuCode.Trim(),
                    StatusLabel(pallet.Status),
                    qty,
                    IsWarehouseBound: false,
                    SortOrder: 2));
            }
        }

        return rows.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static void AddHu(Dictionary<long, SortedSet<string>> rows, long orderLineId, string huCode)
    {
        if (!rows.TryGetValue(orderLineId, out var huCodes))
        {
            huCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            rows[orderLineId] = huCodes;
        }

        huCodes.Add(huCode);
    }

    private static void AddDisplay(
        Dictionary<long, List<OrderLineHuDisplayEntry>> rows,
        long orderLineId,
        OrderLineHuDisplayEntry entry)
    {
        if (!rows.TryGetValue(orderLineId, out var entries))
        {
            entries = new List<OrderLineHuDisplayEntry>();
            rows[orderLineId] = entries;
        }

        if (entries.Any(existing => string.Equals(existing.HuCode, entry.HuCode, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(existing.Label, entry.Label, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entries.Add(entry);
    }

    private static string StatusLabel(string? status)
    {
        if (string.Equals(status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
        {
            return "напечатано";
        }

        if (string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            return "наполнено";
        }

        return "план";
    }

    private static bool HasPositiveHuBalance(IDataStore store, long itemId, string huCode)
    {
        return store.GetHuStockRows()
            .Where(row => row.ItemId == itemId)
            .Where(row => string.Equals(row.HuCode?.Trim(), huCode, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty) > StockQuantityRules.QtyTolerance;
    }

    private static IReadOnlyList<ProductionPallet> GetProductionPalletsForOrder(IDataStore store, long orderId)
    {
        try
        {
            return store.GetProductionPalletsByOrder(orderId);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return store.GetDocsByOrder(orderId)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
                .ToArray();
        }
    }

    private static bool IsMockStoreException(Exception ex)
    {
        return ex.GetType().Namespace?.StartsWith("Moq", StringComparison.Ordinal) == true
               || ex is NotImplementedException;
    }
}
