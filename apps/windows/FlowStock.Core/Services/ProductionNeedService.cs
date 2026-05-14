using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionNeedService(IDataStore dataStore)
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<ProductionNeedRow> GetRows(bool includeZeroNeed = false)
    {
        if (_dataStore is IOptimizedOrderReadModelStore optimizedStore)
        {
            return optimizedStore.GetProductionNeedRows(includeZeroNeed);
        }

        var items = _dataStore.GetItems(null);
        var stockRows = _dataStore.GetStock(null);
        var needByItem = BuildNeedByItem();
        var plannedByItem = BuildPlannedProductionByItem();
        var palletProgressByItem = BuildPalletProgressByItem();
        var openInternalRefsByItem = BuildOpenInternalOrderRefsByItem();

        var stockByItem = stockRows
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    PhysicalStockQty = group.Sum(row => row.Qty),
                    ReservedCustomerOrderQty = group.First().ReservedCustomerOrderQty
                });

        var itemIds = items.Select(item => item.Id)
            .Concat(stockByItem.Keys)
            .Concat(needByItem.Keys)
            .Concat(plannedByItem.Keys)
            .Concat(palletProgressByItem.Keys)
            .Concat(openInternalRefsByItem.Keys)
            .Distinct()
            .ToList();
        var itemsById = items.ToDictionary(item => item.Id);

        return itemIds
            .Select(itemId =>
            {
                itemsById.TryGetValue(itemId, out var item);
                stockByItem.TryGetValue(itemId, out var stockSnapshot);
                needByItem.TryGetValue(itemId, out var currentNeed);
                plannedByItem.TryGetValue(itemId, out var planned);
                palletProgressByItem.TryGetValue(itemId, out var palletProgress);
                openInternalRefsByItem.TryGetValue(itemId, out var openInternalRefs);

                var physicalStockQty = stockSnapshot?.PhysicalStockQty ?? 0;
                var reservedCustomerOrderQty = stockSnapshot?.ReservedCustomerOrderQty ?? 0;
                var freeStockQty = physicalStockQty - reservedCustomerOrderQty;
                var minStockQty = item?.ItemTypeEnableMinStockControl == true
                    ? Math.Max(0, item.MinStockQty ?? 0)
                    : 0;
                var rawToCloseOrdersQty = Math.Max(0, currentNeed);
                var rawToMinStockQty = Math.Max(0, minStockQty - freeStockQty);
                var plannedProductionQty = Math.Max(0, planned);
                palletProgress ??= new PalletProgress();
                var toCloseOrdersQty = rawToCloseOrdersQty;
                var toMinStockQty = Math.Max(0, rawToMinStockQty - plannedProductionQty);
                var qtyToCreate = toMinStockQty;
                var canCreateOrder = qtyToCreate > QtyTolerance;
                var itemTypeName = string.IsNullOrWhiteSpace(item?.ItemTypeName) ? "Без типа" : item!.ItemTypeName!;
                var reason = BuildReason(minStockQty, rawToMinStockQty, plannedProductionQty, qtyToCreate);

                return new ProductionNeedRow
                {
                    ItemId = itemId,
                    NeedDate = DateTime.Today,
                    Gtin = item?.Gtin,
                    ItemName = item?.Name ?? $"#{itemId}",
                    ItemTypeName = itemTypeName,
                    FreeStockQty = freeStockQty,
                    MinStockQty = minStockQty,
                    ToCloseOrdersQty = toCloseOrdersQty,
                    ToMinStockQty = toMinStockQty,
                    OpenInternalOrderQty = plannedProductionQty,
                    OpenInternalOrderRefs = openInternalRefs ?? string.Empty,
                    PlannedPalletQty = palletProgress.PlannedQty,
                    FilledPalletQty = palletProgress.FilledQty,
                    PlannedPalletCount = palletProgress.PlannedPalletCount,
                    FilledPalletCount = palletProgress.FilledPalletCount,
                    RemainingPalletQty = palletProgress.RemainingQty,
                    QtyToCreate = qtyToCreate,
                    CanCreateOrder = canCreateOrder,
                    Reason = reason,
                    TotalToMakeQty = toCloseOrdersQty + toMinStockQty
                };
            })
            .Where(row => includeZeroNeed
                          || row.TotalToMakeQty > 0
                          || row.OpenInternalOrderQty > 0
                          || row.PlannedPalletQty > 0
                          || row.FilledPalletQty > 0)
            .OrderByDescending(row => row.TotalToMakeQty)
            .ThenBy(row => row.ItemTypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();
    }

    private Dictionary<long, double> BuildNeedByItem()
    {
        var result = new Dictionary<long, double>();
        var activeCustomerOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status != OrderStatus.Draft
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled);

        foreach (var order in activeCustomerOrders)
        {
            foreach (var line in OrderReceiptRemainingCalculator.GetRemaining(_dataStore, order))
            {
                var remainingQty = Math.Max(0, line.QtyRemaining);
                if (remainingQty <= QtyTolerance)
                {
                    continue;
                }

                result[line.ItemId] = result.TryGetValue(line.ItemId, out var current)
                    ? current + remainingQty
                    : remainingQty;
            }
        }

        return result;
    }

    private Dictionary<long, double> BuildPlannedProductionByItem()
    {
        var result = new Dictionary<long, double>();
        var activePlannedOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled);

        foreach (var order in activePlannedOrders)
        {
            var receiptRemainingByLine = OrderReceiptRemainingCalculator.GetRemaining(_dataStore, order)
                .ToDictionary(line => line.OrderLineId);

            foreach (var line in _dataStore.GetOrderLines(order.Id))
            {
                var remainingQty = receiptRemainingByLine.TryGetValue(line.Id, out var receiptLine)
                    ? Math.Max(0, receiptLine.QtyRemaining)
                    : Math.Max(0, line.QtyOrdered);
                if (remainingQty <= 0)
                {
                    continue;
                }

                result[line.ItemId] = result.TryGetValue(line.ItemId, out var current)
                    ? current + remainingQty
                    : remainingQty;
            }
        }

        return result;
    }

    private Dictionary<long, PalletProgress> BuildPalletProgressByItem()
    {
        var result = new Dictionary<long, PalletProgress>();
        foreach (var workItem in _dataStore.GetActiveProductionPalletWorkItems())
        {
            if (workItem.Summary.PlannedPalletCount <= 0 && workItem.Summary.PlannedQty <= QtyTolerance)
            {
                continue;
            }

            foreach (var pallet in _dataStore.GetProductionPalletsByDoc(workItem.PrdDocId)
                         .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)))
            {
                if (pallet.Lines.Count > 0)
                {
                    foreach (var line in pallet.Lines)
                    {
                        if (line.PlannedQty <= QtyTolerance && line.FilledQty <= QtyTolerance)
                        {
                            continue;
                        }

                        var currentProgress = result.TryGetValue(line.ItemId, out var existingProgress)
                            ? existingProgress
                            : new PalletProgress();
                        result[line.ItemId] = currentProgress with
                        {
                            PlannedQty = currentProgress.PlannedQty + Math.Max(0, line.PlannedQty),
                            FilledQty = currentProgress.FilledQty + Math.Max(0, line.FilledQty > QtyTolerance ? line.FilledQty : 0),
                            RemainingQty = currentProgress.RemainingQty + Math.Max(0, line.PlannedQty - (line.FilledQty > QtyTolerance ? line.FilledQty : 0)),
                            PlannedPalletCount = currentProgress.PlannedPalletCount + 1,
                            FilledPalletCount = currentProgress.FilledPalletCount + (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        };
                    }

                    continue;
                }

                if (pallet.PlannedQty <= QtyTolerance)
                {
                    continue;
                }

                var palletProgress = result.TryGetValue(pallet.ItemId, out var existing)
                    ? existing
                    : new PalletProgress();
                var isFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
                result[pallet.ItemId] = palletProgress with
                {
                    PlannedQty = palletProgress.PlannedQty + pallet.PlannedQty,
                    FilledQty = palletProgress.FilledQty + (isFilled ? pallet.PlannedQty : 0),
                    RemainingQty = palletProgress.RemainingQty + (isFilled ? 0 : pallet.PlannedQty),
                    PlannedPalletCount = palletProgress.PlannedPalletCount + 1,
                    FilledPalletCount = palletProgress.FilledPalletCount + (isFilled ? 1 : 0)
                };
            }
        }

        return result;
    }

    private Dictionary<long, string> BuildOpenInternalOrderRefsByItem()
    {
        var refsByItem = new Dictionary<long, HashSet<string>>();
        var activeInternalOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled)
            .ToList();

        foreach (var order in activeInternalOrders)
        {
            var remainingByLine = OrderReceiptRemainingCalculator.GetRemaining(_dataStore, order)
                .ToDictionary(line => line.OrderLineId);
            foreach (var line in _dataStore.GetOrderLines(order.Id))
            {
                var remainingQty = remainingByLine.TryGetValue(line.Id, out var receiptLine)
                    ? Math.Max(0, receiptLine.QtyRemaining)
                    : Math.Max(0, line.QtyOrdered);
                if (remainingQty <= QtyTolerance)
                {
                    continue;
                }

                if (!refsByItem.TryGetValue(line.ItemId, out var refs))
                {
                    refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    refsByItem[line.ItemId] = refs;
                }

                if (!string.IsNullOrWhiteSpace(order.OrderRef))
                {
                    refs.Add(order.OrderRef.Trim());
                }
            }
        }

        return refsByItem.ToDictionary(
            pair => pair.Key,
            pair => string.Join(", ", pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
    }

    private static string BuildReason(double minStockQty, double rawToMinStockQty, double plannedProductionQty, double qtyToCreate)
    {
        if (qtyToCreate > QtyTolerance)
        {
            return "Требуется пополнение склада до минимального остатка.";
        }

        if (minStockQty <= QtyTolerance)
        {
            return "Для товара не задан минимальный остаток.";
        }

        if (rawToMinStockQty <= QtyTolerance)
        {
            return "Свободный остаток уже покрывает минимальный уровень.";
        }

        if (plannedProductionQty > QtyTolerance)
        {
            return "Потребность уже покрыта открытой внутренней работой.";
        }

        return "Нет строк для создания внутреннего заказа.";
    }

    private sealed record PalletProgress(
        double PlannedQty = 0,
        double FilledQty = 0,
        double RemainingQty = 0,
        int PlannedPalletCount = 0,
        int FilledPalletCount = 0);
}
