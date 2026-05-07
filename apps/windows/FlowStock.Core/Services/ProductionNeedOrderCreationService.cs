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
        var draftLines = BuildDraftLines();
        if (draftLines.Count == 0)
        {
            return new ProductionNeedOrderCreationResult
            {
                Message = "Новой потребности для формирования нет."
            };
        }

        var orderService = new OrderService(_dataStore);
        var internalOrderId = orderService.CreateDraftOrder(
            GenerateNextOrderRef(),
            null,
            null,
            "Автосформировано из потребности производства.",
            draftLines,
            OrderType.Internal);

        return new ProductionNeedOrderCreationResult
        {
            CustomerDraftCount = 0,
            InternalDraftCount = 1,
            CreatedLineCount = draftLines.Count,
            InternalDraftOrderId = internalOrderId,
            Message = BuildMessage(draftLines.Count)
        };
    }

    private List<OrderLineView> BuildDraftLines()
    {
        return new ProductionNeedService(_dataStore)
            .GetRows(includeZeroNeed: false)
            .Where(row => row.ToMinStockQty > QtyTolerance)
            .Select(row =>
            {
                var item = _dataStore.FindItemById(row.ItemId) ?? throw new InvalidOperationException("Товар потребности не найден.");
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

    private string GenerateNextOrderRef()
    {
        long max = 0;
        foreach (var order in _dataStore.GetOrders())
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

    private static string BuildMessage(int createdLineCount)
    {
        return createdLineCount == 0
            ? "Новой потребности для формирования нет."
            : $"Создан внутренний черновик на склад: строк {createdLineCount}.";
    }
}
