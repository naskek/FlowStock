using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public sealed class MarkingNeedCreationService(IDataStore dataStore)
{
    private const double QtyTolerance = 0.000001;
    public const string ProductionNeedSourceType = "PRODUCTION_NEED";
    public const string ProductionOrderSourceType = "PRODUCTION_ORDER";
    private readonly IDataStore _dataStore = dataStore;

    public MarkingNeedCreationResult CreateFromProductionNeeds(DateTime createdAt)
    {
        var requiredByItem = BuildRequiredQtyByItem();
        if (requiredByItem.Count == 0)
        {
            return new MarkingNeedCreationResult
            {
                DebugSummary = Array.Empty<string>(),
                Message = "Нет маркируемой производственной потребности."
            };
        }

        var coverageByItem = BuildCoverageByItem(requiredByItem);

        var createdCount = 0;
        var createdQty = 0d;
        var debugSummary = new List<string>();
        foreach (var pair in requiredByItem.OrderBy(pair => pair.Value.ItemName, StringComparer.CurrentCultureIgnoreCase))
        {
            var itemId = pair.Key;
            var item = pair.Value;
            var requiredQty = item.RequiredQty;
            coverageByItem.TryGetValue(itemId, out var coverage);
            var existingActiveTaskQty = coverage?.ExistingActiveTaskQty ?? 0d;
            var freeCodeQty = coverage?.FreeCodeQty ?? 0d;
            var qtyToCreate = freeCodeQty + QtyTolerance >= requiredQty
                ? 0d
                : Math.Max(0, requiredQty - existingActiveTaskQty);

            debugSummary.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"item_id={itemId}; gtin={item.Gtin}; item={item.ItemName}; production_need_qty={item.ProductionNeedQty:0.###}; open_internal_remaining_qty={item.OpenInternalRemainingQty:0.###}; required_marking_qty={requiredQty:0.###}; existing_active_task_qty={existingActiveTaskQty:0.###}; free_code_qty={freeCodeQty:0.###}; created_qty={qtyToCreate:0.###}"));

            if (qtyToCreate <= QtyTolerance)
            {
                continue;
            }

            var requestedQuantity = (int)Math.Ceiling(qtyToCreate - QtyTolerance);
            _dataStore.AddMarkingOrder(new MarkingOrder
            {
                Id = Guid.NewGuid(),
                OrderId = null,
                ItemId = itemId,
                Gtin = item.Gtin,
                RequestedQuantity = requestedQuantity,
                RequestNumber = BuildRequestNumber(itemId, createdAt, createdCount + 1),
                Status = MarkingOrderStatus.WaitingForCodes,
                Notes = "Автосформировано по производственной потребности.",
                SourceType = ProductionNeedSourceType,
                RequestedAt = createdAt,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });

            createdCount++;
            createdQty += requestedQuantity;
        }

        return new MarkingNeedCreationResult
        {
            CreatedTaskCount = createdCount,
            CreatedQty = createdQty,
            DebugSummary = debugSummary,
            Message = createdCount == 0
                ? "Новой маркировки для формирования нет."
                : $"Создано задач маркировки: {createdCount}, кодов: {createdQty:0.###}."
        };
    }

    private Dictionary<long, RequiredMarkingItem> BuildRequiredQtyByItem()
    {
        var plannedByItem = BuildOpenInternalProductionByItem();
        var rows = new ProductionNeedService(_dataStore).GetRows(includeZeroNeed: true);
        var result = new Dictionary<long, RequiredMarkingItem>();

        foreach (var row in rows)
        {
            plannedByItem.TryGetValue(row.ItemId, out var openInternalRemainingQty);
            var productionNeedQty = Math.Max(0, row.TotalToMakeQty);
            var requiredQty = Math.Max(productionNeedQty, openInternalRemainingQty);
            AddRequiredItem(result, row.ItemId, productionNeedQty, openInternalRemainingQty, requiredQty);
        }

        foreach (var pair in plannedByItem)
        {
            AddRequiredItem(result, pair.Key, 0d, pair.Value, pair.Value);
        }

        return result
            .Where(pair => pair.Value.RequiredQty > QtyTolerance)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private void AddRequiredItem(
        Dictionary<long, RequiredMarkingItem> result,
        long itemId,
        double productionNeedQty,
        double openInternalRemainingQty,
        double requiredQty)
    {
        if (requiredQty <= QtyTolerance)
        {
            return;
        }

        var item = _dataStore.FindItemById(itemId);
        if (item?.ItemTypeEnableMarking != true || string.IsNullOrWhiteSpace(item.Gtin))
        {
            return;
        }

        if (result.TryGetValue(itemId, out var current))
        {
            result[itemId] = current with
            {
                ProductionNeedQty = Math.Max(current.ProductionNeedQty, productionNeedQty),
                OpenInternalRemainingQty = Math.Max(current.OpenInternalRemainingQty, openInternalRemainingQty),
                RequiredQty = Math.Max(current.RequiredQty, requiredQty)
            };
            return;
        }

        result[itemId] = new RequiredMarkingItem(
            item.Name,
            item.Gtin.Trim(),
            productionNeedQty,
            openInternalRemainingQty,
            requiredQty);
    }

    private Dictionary<long, double> BuildOpenInternalProductionByItem()
    {
        var result = new Dictionary<long, double>();
        var activeOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Status is not OrderStatus.Shipped and not OrderStatus.Cancelled);

        foreach (var order in activeOrders)
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

                result[line.ItemId] = result.TryGetValue(line.ItemId, out var current)
                    ? current + remainingQty
                    : remainingQty;
            }
        }

        return result;
    }

    private Dictionary<long, ExistingCoverage> BuildCoverageByItem(IReadOnlyDictionary<long, RequiredMarkingItem> requiredByItem)
    {
        var result = new Dictionary<long, ExistingCoverage>();

        var ordersByItem = _dataStore.GetMarkingOrdersByItemIds(requiredByItem.Keys.ToArray())
            .Where(order => order.ItemId.HasValue
                            && requiredByItem.ContainsKey(order.ItemId.Value)
                            && IsCurrentProductionSource(order)
                            && IsCoveringStatus(order.Status));

        foreach (var order in ordersByItem)
        {
            var itemId = order.ItemId!.Value;
            var requestedQty = Math.Max(0, order.RequestedQuantity);
            var codesTotal = _dataStore.CountMarkingCodesByMarkingOrder(order.Id);
            if (requestedQty <= QtyTolerance || codesTotal + QtyTolerance >= requestedQty)
            {
                continue;
            }

            result[itemId] = result.TryGetValue(itemId, out var current)
                ? current with { ExistingActiveTaskQty = current.ExistingActiveTaskQty + requestedQty }
                : new ExistingCoverage(requestedQty, 0d);
        }

        foreach (var pair in requiredByItem)
        {
            var freeCodeQty = Math.Max(0, _dataStore.CountFreeProductionMarkingCodesByItem(pair.Key, pair.Value.Gtin));
            result[pair.Key] = result.TryGetValue(pair.Key, out var current)
                ? current with { FreeCodeQty = freeCodeQty }
                : new ExistingCoverage(0d, freeCodeQty);
        }

        return result;
    }

    private static bool IsCoveringStatus(string? status)
    {
        return !string.Equals(status, MarkingOrderStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(status, MarkingOrderStatus.Failed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentProductionSource(MarkingOrder order)
    {
        return string.Equals(order.SourceType, ProductionNeedSourceType, StringComparison.OrdinalIgnoreCase)
               || string.Equals(order.SourceType, ProductionOrderSourceType, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRequestNumber(long itemId, DateTime createdAt, int sequence)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"PN-{itemId}-{createdAt:yyyyMMddHHmmssfff}-{sequence:000}");
    }

    private sealed record RequiredMarkingItem(
        string ItemName,
        string Gtin,
        double ProductionNeedQty,
        double OpenInternalRemainingQty,
        double RequiredQty);

    private sealed record ExistingCoverage(double ExistingActiveTaskQty, double FreeCodeQty);
}
