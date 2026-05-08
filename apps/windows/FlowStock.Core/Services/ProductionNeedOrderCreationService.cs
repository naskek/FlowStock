using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionNeedOrderCreationService(IDataStore dataStore)
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _dataStore = dataStore;

    public ProductionNeedOrderCreationResult CreateDraftOrders()
    {
        ProductionNeedOrderCreationResult? result = null;
        _dataStore.ExecuteInTransaction(store =>
        {
            result = CreateDraftOrdersInTransaction(store);
        });

        return result ?? throw new InvalidOperationException("Не удалось выполнить формирование производственной потребности.");
    }

    private static ProductionNeedOrderCreationResult CreateDraftOrdersInTransaction(IDataStore dataStore)
    {
        var currentRows = new ProductionNeedService(dataStore)
            .GetRows(includeZeroNeed: false);
        var openInternalByItem = BuildOpenInternalProductionByItem(dataStore);
        var debugSummary = BuildDebugSummary(currentRows, openInternalByItem);
        var draftLines = BuildDraftLines(dataStore, currentRows);
        long? internalOrderId = null;
        if (draftLines.Count > 0)
        {
            var orderService = new OrderService(dataStore);
            internalOrderId = orderService.CreateDraftOrder(
                GenerateNextOrderRef(dataStore),
                null,
                null,
                "Автосформировано из потребности производства.",
                draftLines,
                OrderType.Internal);
        }

        return new ProductionNeedOrderCreationResult
        {
            CustomerDraftCount = 0,
            InternalDraftCount = internalOrderId.HasValue ? 1 : 0,
            CreatedLineCount = draftLines.Count,
            InternalDraftOrderId = internalOrderId,
            DebugSummary = debugSummary,
            Message = BuildMessage(draftLines.Count)
        };
    }

    private static List<OrderLineView> BuildDraftLines(IDataStore dataStore, IReadOnlyList<ProductionNeedRow> currentRows)
    {
        return currentRows
            .Where(row => row.ToMinStockQty > QtyTolerance)
            .Select(row =>
            {
                var item = dataStore.FindItemById(row.ItemId) ?? throw new InvalidOperationException("Товар потребности не найден.");
                return new OrderLineView
                {
                    ItemId = row.ItemId,
                    ItemName = item.Name,
                    QtyOrdered = row.ToMinStockQty,
                    ProductionPurpose = ProductionLinePurpose.InternalStock
                };
            })
            .ToList();
    }

    private static string GenerateNextOrderRef(IDataStore dataStore)
    {
        long max = 0;
        foreach (var order in dataStore.GetOrders())
        {
            var orderRef = order.OrderRef?.Trim();
            if (string.IsNullOrWhiteSpace(orderRef))
            {
                continue;
            }

            if (!orderRef.All(char.IsDigit))
            {
                continue;
            }

            if (long.TryParse(orderRef, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value > max)
            {
                max = value;
            }
        }

        return (max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    private static Dictionary<long, double> BuildOpenInternalProductionByItem(IDataStore dataStore)
    {
        var result = new Dictionary<long, double>();
        var activeOrders = dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled);

        foreach (var order in activeOrders)
        {
            var remainingByLine = OrderReceiptRemainingCalculator.GetRemaining(dataStore, order)
                .ToDictionary(line => line.OrderLineId);

            foreach (var line in dataStore.GetOrderLines(order.Id))
            {
                var remainingQty = remainingByLine.TryGetValue(line.Id, out var receiptLine)
                    ? Math.Max(0, receiptLine.QtyRemaining)
                    : Math.Max(0, line.QtyOrdered);
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

    private static IReadOnlyList<string> BuildDebugSummary(
        IReadOnlyList<ProductionNeedRow> currentRows,
        IReadOnlyDictionary<long, double> openInternalByItem)
    {
        return currentRows
            .Select(row =>
            {
                openInternalByItem.TryGetValue(row.ItemId, out var openInternalPlanned);
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"item_id={row.ItemId}; gtin={row.Gtin ?? ""}; item={row.ItemName}; to_close_orders={row.ToCloseOrdersQty:0.###}; to_min_stock={row.ToMinStockQty:0.###}; total_to_make={row.TotalToMakeQty:0.###}; open_internal_planned={openInternalPlanned:0.###}; internal_draft_qty_to_create={row.ToMinStockQty:0.###}");
            })
            .ToArray();
    }

    private static string BuildMessage(int createdLineCount)
    {
        return createdLineCount == 0
            ? "Внутренний черновик не создан: по актуальной потребности нет строк \"На склад до мин.\"."
            : $"Создан внутренний черновик на склад: строк {createdLineCount}.";
    }
}
