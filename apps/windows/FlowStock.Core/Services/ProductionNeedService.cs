using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionNeedService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<ProductionNeedRow> GetRows(bool includeZeroNeed = false)
    {
        var items = _dataStore.GetItems(null);
        var stockRows = _dataStore.GetStock(null);
        var needByItem = BuildNeedByItem();
        var plannedByItem = BuildPlannedProductionByItem();

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

                var physicalStockQty = stockSnapshot?.PhysicalStockQty ?? 0;
                var reservedCustomerOrderQty = stockSnapshot?.ReservedCustomerOrderQty ?? 0;
                var freeStockQty = physicalStockQty - reservedCustomerOrderQty;
                var minStockQty = item?.ItemTypeEnableMinStockControl == true
                    ? Math.Max(0, item.MinStockQty ?? 0)
                    : 0;
                var rawToCloseOrdersQty = Math.Max(0, currentNeed.OrderQty - currentNeed.ReservedQty);
                var rawToMinStockQty = Math.Max(0, minStockQty - freeStockQty);
                var plannedProductionQty = Math.Max(0, planned);
                var plannedForOrders = Math.Min(rawToCloseOrdersQty, plannedProductionQty);
                var remainingPlannedQty = Math.Max(0, plannedProductionQty - plannedForOrders);
                var toCloseOrdersQty = Math.Max(0, rawToCloseOrdersQty - plannedForOrders);
                var toMinStockQty = Math.Max(0, rawToMinStockQty - remainingPlannedQty);
                var itemTypeName = string.IsNullOrWhiteSpace(item?.ItemTypeName) ? "Без типа" : item!.ItemTypeName!;

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
                    TotalToMakeQty = toCloseOrdersQty + toMinStockQty
                };
            })
            .Where(row => includeZeroNeed || row.TotalToMakeQty > 0)
            .OrderByDescending(row => row.TotalToMakeQty)
            .ThenBy(row => row.ItemTypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();
    }

    private Dictionary<long, (double OrderQty, double ReservedQty)> BuildNeedByItem()
    {
        var result = new Dictionary<long, (double OrderQty, double ReservedQty)>();
        var activeCustomerOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status != OrderStatus.Draft
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled);

        foreach (var order in activeCustomerOrders)
        {
            var shippedQtyByLine = _dataStore.GetShippedTotalsByOrderLine(order.Id);
            var reservedQtyByLine = _dataStore.GetOrderReceiptPlanLines(order.Id)
                .GroupBy(line => line.OrderLineId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(line => line.QtyPlanned));

            foreach (var line in _dataStore.GetOrderLines(order.Id))
            {
                var shippedQty = shippedQtyByLine.TryGetValue(line.Id, out var value) ? value : 0;
                var openQty = Math.Max(0, line.QtyOrdered - shippedQty);
                var reservedQty = reservedQtyByLine.TryGetValue(line.Id, out var reservedValue)
                    ? Math.Max(0, reservedValue)
                    : 0;

                if (openQty <= 0 && reservedQty <= 0)
                {
                    continue;
                }

                result.TryGetValue(line.ItemId, out var current);
                result[line.ItemId] = (current.OrderQty + openQty, current.ReservedQty + reservedQty);
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
            var receiptRemainingByLine = _dataStore.GetOrderReceiptRemaining(order.Id)
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
}
