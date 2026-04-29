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
        var activeCustomerOpenQtyByItem = BuildActiveCustomerOpenQtyByItem();

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
            .Concat(activeCustomerOpenQtyByItem.Keys)
            .Distinct()
            .ToList();
        var itemsById = items.ToDictionary(item => item.Id);

        var rows = itemIds
            .Select(itemId =>
            {
                itemsById.TryGetValue(itemId, out var item);
                stockByItem.TryGetValue(itemId, out var stockSnapshot);
                activeCustomerOpenQtyByItem.TryGetValue(itemId, out var activeCustomerOrderOpenQty);

                var physicalStockQty = stockSnapshot?.PhysicalStockQty ?? 0;
                var reservedCustomerOrderQty = stockSnapshot?.ReservedCustomerOrderQty ?? 0;
                var freeStockQty = physicalStockQty - reservedCustomerOrderQty;
                var minStockQty = item?.ItemTypeEnableMinStockControl == true
                    ? Math.Max(0, item.MinStockQty ?? 0)
                    : 0;
                var productionNeedQty = Math.Max(0, activeCustomerOrderOpenQty + minStockQty - physicalStockQty);

                return new ProductionNeedRow
                {
                    ItemId = itemId,
                    ItemName = item?.Name ?? $"#{itemId}",
                    ItemTypeName = item?.ItemTypeName,
                    PhysicalStockQty = physicalStockQty,
                    ActiveCustomerOrderOpenQty = activeCustomerOrderOpenQty,
                    ReservedCustomerOrderQty = reservedCustomerOrderQty,
                    FreeStockQty = freeStockQty,
                    MinStockQty = minStockQty,
                    ProductionNeedQty = productionNeedQty
                };
            })
            .Where(row => includeZeroNeed || row.ProductionNeedQty > 0)
            .OrderByDescending(row => row.ProductionNeedQty)
            .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();

        return rows;
    }

    private Dictionary<long, double> BuildActiveCustomerOpenQtyByItem()
    {
        var result = new Dictionary<long, double>();
        var activeCustomerOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer && order.Status != OrderStatus.Shipped);

        foreach (var order in activeCustomerOrders)
        {
            var shippedQtyByLine = _dataStore.GetShippedTotalsByOrderLine(order.Id);

            foreach (var line in _dataStore.GetOrderLines(order.Id))
            {
                var shippedQty = shippedQtyByLine.TryGetValue(line.Id, out var value) ? value : 0;
                var openQty = Math.Max(0, line.QtyOrdered - shippedQty);
                if (openQty <= 0)
                {
                    continue;
                }

                result[line.ItemId] = result.TryGetValue(line.ItemId, out var currentQty)
                    ? currentQty + openQty
                    : openQty;
            }
        }

        return result;
    }
}
