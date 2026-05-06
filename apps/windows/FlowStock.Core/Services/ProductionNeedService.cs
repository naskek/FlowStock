using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionNeedService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<ProductionNeedRow> GetRows(bool includeZeroNeed = false)
    {
        var today = DateTime.Today;
        var items = _dataStore.GetItems(null);
        var stockRows = _dataStore.GetStock(null);
        var needByKey = BuildNeedByItemAndDate(today);

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
            .Concat(needByKey.Keys.Select(key => key.ItemId))
            .Distinct()
            .ToList();
        var itemsById = items.ToDictionary(item => item.Id);

        var rows = new List<ProductionNeedRow>();
        foreach (var itemId in itemIds)
        {
            itemsById.TryGetValue(itemId, out var item);
            stockByItem.TryGetValue(itemId, out var stockSnapshot);

            var physicalStockQty = stockSnapshot?.PhysicalStockQty ?? 0;
            var reservedCustomerOrderQty = stockSnapshot?.ReservedCustomerOrderQty ?? 0;
            var freeStockQty = physicalStockQty - reservedCustomerOrderQty;
            var minStockQty = item?.ItemTypeEnableMinStockControl == true
                ? Math.Max(0, item.MinStockQty ?? 0)
                : 0;
            var toMinStockQty = Math.Max(0, minStockQty - freeStockQty);
            var itemTypeName = string.IsNullOrWhiteSpace(item?.ItemTypeName) ? "Без типа" : item!.ItemTypeName!;

            var dates = needByKey.Keys
                .Where(key => key.ItemId == itemId)
                .Select(key => key.NeedDate)
                .Append(today)
                .Distinct()
                .OrderBy(date => date)
                .ToList();

            foreach (var date in dates)
            {
                needByKey.TryGetValue((itemId, date), out var dateNeed);
                var toCloseOrdersQty = Math.Max(0, dateNeed.OrderQty - dateNeed.ReservedQty);
                var rowToMinStockQty = date == today ? toMinStockQty : 0;
                var totalToMakeQty = toCloseOrdersQty + rowToMinStockQty;

                rows.Add(new ProductionNeedRow
                {
                    ItemId = itemId,
                    NeedDate = date,
                    Gtin = item?.Gtin,
                    ItemName = item?.Name ?? $"#{itemId}",
                    ItemTypeName = itemTypeName,
                    FreeStockQty = freeStockQty,
                    MinStockQty = minStockQty,
                    ToCloseOrdersQty = toCloseOrdersQty,
                    ToMinStockQty = rowToMinStockQty,
                    TotalToMakeQty = totalToMakeQty
                });
            }
        }

        return rows
            .Where(row => includeZeroNeed || row.TotalToMakeQty > 0)
            .OrderBy(row => row.NeedDate)
            .ThenByDescending(row => row.TotalToMakeQty)
            .ThenBy(row => row.ItemTypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();
    }

    private Dictionary<(long ItemId, DateTime NeedDate), (double OrderQty, double ReservedQty)> BuildNeedByItemAndDate(DateTime today)
    {
        var result = new Dictionary<(long ItemId, DateTime NeedDate), (double OrderQty, double ReservedQty)>();
        var activeCustomerOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled);

        foreach (var order in activeCustomerOrders)
        {
            var needDate = order.DueDate?.Date ?? today;
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

                var key = (line.ItemId, needDate);
                result.TryGetValue(key, out var current);
                result[key] = (current.OrderQty + openQty, current.ReservedQty + reservedQty);
            }
        }

        return result;
    }
}
