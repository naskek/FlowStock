using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

internal static class OrderCoverageProducedCompensationService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;

    internal static int CompensateAll(
        IDataStore store,
        long targetOrderId,
        IReadOnlyList<OrderCoverageTransfer> transfers)
    {
        var selected = transfers
            .Where(IsUncompensatedAdoption)
            .Where(transfer => transfer.TargetOrderId == targetOrderId)
            .OrderBy(transfer => transfer.Id)
            .ToArray();
        if (selected.Length == 0)
        {
            return 0;
        }

        var sourceOrders = selected
            .Select(transfer => transfer.SourceOrderId)
            .Distinct()
            .ToDictionary(
                sourceOrderId => sourceOrderId,
                sourceOrderId => RequireSourceOrder(store, sourceOrderId));

        var restoredLineIds = ResolveRestoredSourceOrderLines(store, selected, sourceOrders);
        var qtyToRestoreByLineId = new Dictionary<long, double>();
        var compensatedCount = 0;

        foreach (var transfer in selected)
        {
            var lines = transfer.Lines
                .Select(line =>
                {
                    if (!restoredLineIds.TryGetValue(
                            (transfer.SourceOrderId, line.SourceOrderLineId),
                            out var restoredSourceOrderLineId))
                    {
                        throw new InvalidOperationException(
                            "Нельзя компенсировать produced coverage: не восстановлена INTERNAL line.");
                    }

                    return new OrderCoverageProducedCompensationLine
                    {
                        TransferLineId = line.Id,
                        HistoricalSourceOrderLineId = line.SourceOrderLineId,
                        TargetOrderLineId = line.TargetOrderLineId,
                        SourceDocLineId = line.SourceDocLineId,
                        SourceProductionPalletLineId = line.SourceProductionPalletLineId,
                        RestoredSourceOrderLineId = restoredSourceOrderLineId,
                        ItemId = line.ItemId,
                        TransferredQty = line.TransferredQty,
                        SourceProductionPurpose = ProductionLinePurposeMapper.FromDbValue(
                            line.SourceProductionPurpose),
                        SourceProductionPalletGroup = NormalizeGroup(line.SourceProductionPalletGroup)
                    };
                })
                .ToArray();

            if (lines.Length == 0)
            {
                throw new InvalidOperationException(
                    "Нельзя компенсировать produced coverage: provenance не содержит component lines.");
            }

            var applied = store.CompensateProducedOrderCoverageTransfer(
                new OrderCoverageProducedCompensation
                {
                    TransferId = transfer.Id,
                    SourceOrderId = transfer.SourceOrderId,
                    TargetOrderId = transfer.TargetOrderId,
                    TargetPrdDocId = transfer.TargetPrdDocId,
                    ProductionPalletId = transfer.ProductionPalletId,
                    HuCode = transfer.HuCode,
                    Lines = lines
                },
                DateTime.UtcNow);
            if (!applied)
            {
                throw new InvalidOperationException(
                    "Produced coverage изменился одновременно с compensation. Повторите операцию.");
            }

            compensatedCount++;
            foreach (var line in lines)
            {
                qtyToRestoreByLineId[line.RestoredSourceOrderLineId] =
                    qtyToRestoreByLineId.GetValueOrDefault(line.RestoredSourceOrderLineId)
                    + Math.Max(0, line.TransferredQty);
            }
        }

        foreach (var pair in qtyToRestoreByLineId)
        {
            var line = sourceOrders.Values
                .SelectMany(order => store.GetOrderLines(order.Id))
                .FirstOrDefault(candidate => candidate.Id == pair.Key)
                ?? throw new InvalidOperationException(
                    "Нельзя восстановить INTERNAL demand: строка заказа исчезла во время produced compensation.");
            store.UpdateOrderLineQty(line.Id, line.QtyOrdered + pair.Value);
        }

        foreach (var sourceOrder in sourceOrders.Values)
        {
            if (sourceOrder.Status == OrderStatus.Merged)
            {
                var snapshotStatuses = selected
                    .Where(transfer => transfer.SourceOrderId == sourceOrder.Id)
                    .Select(transfer => OrderStatusMapper.StatusFromString(transfer.SourceOrderStatus))
                    .Where(status => status.HasValue)
                    .Select(status => status!.Value)
                    .ToArray();
                var reactivatedStatus = snapshotStatuses.Contains(OrderStatus.InProgress)
                    ? OrderStatus.InProgress
                    : snapshotStatuses.Length > 0 && snapshotStatuses.All(status => status == OrderStatus.Draft)
                        ? OrderStatus.Draft
                        : OrderStatus.InProgress;
                store.UpdateOrderStatus(sourceOrder.Id, reactivatedStatus);
            }

            new OrderService(store).RefreshPersistedStatus(sourceOrder.Id);
        }

        return compensatedCount;
    }

    private static Dictionary<(long SourceOrderId, long HistoricalSourceOrderLineId), long>
        ResolveRestoredSourceOrderLines(
            IDataStore store,
            IReadOnlyList<OrderCoverageTransfer> transfers,
            IReadOnlyDictionary<long, Order> sourceOrders)
    {
        var result = new Dictionary<(long, long), long>();
        var currentLinesByOrder = sourceOrders.Keys.ToDictionary(
            orderId => orderId,
            orderId => store.GetOrderLines(orderId).ToList());

        foreach (var transfer in transfers)
        {
            foreach (var provenanceLine in transfer.Lines)
            {
                var key = (transfer.SourceOrderId, provenanceLine.SourceOrderLineId);
                if (result.ContainsKey(key))
                {
                    continue;
                }

                var purpose = ProductionLinePurposeMapper.FromDbValue(provenanceLine.SourceProductionPurpose);
                var normalizedGroup = NormalizeGroup(provenanceLine.SourceProductionPalletGroup);
                var lines = currentLinesByOrder[transfer.SourceOrderId];
                var original = lines.FirstOrDefault(line => line.Id == provenanceLine.SourceOrderLineId);
                if (original != null)
                {
                    EnsureSourceLineMatchesProvenance(original, provenanceLine, purpose, normalizedGroup);
                    result[key] = original.Id;
                    continue;
                }

                var equivalents = lines
                    .Where(line => line.ItemId == provenanceLine.ItemId)
                    .Where(line => line.ProductionPurpose == purpose)
                    .Where(line => string.Equals(
                        NormalizeGroup(line.ProductionPalletGroup),
                        normalizedGroup,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (equivalents.Length > 1)
                {
                    throw new InvalidOperationException(
                        "Нельзя восстановить INTERNAL demand автоматически: найдено несколько эквивалентных строк.");
                }

                if (equivalents.Length == 1)
                {
                    result[key] = equivalents[0].Id;
                    continue;
                }

                var restoredId = store.AddOrderLine(new OrderLine
                {
                    OrderId = transfer.SourceOrderId,
                    ItemId = provenanceLine.ItemId,
                    QtyOrdered = 0,
                    ProductionPurpose = purpose,
                    ProductionPalletGroup = normalizedGroup
                });
                if (restoredId <= 0)
                {
                    throw new InvalidOperationException(
                        "Не удалось восстановить INTERNAL line для produced compensation.");
                }

                lines.Add(new OrderLine
                {
                    Id = restoredId,
                    OrderId = transfer.SourceOrderId,
                    ItemId = provenanceLine.ItemId,
                    QtyOrdered = 0,
                    ProductionPurpose = purpose,
                    ProductionPalletGroup = normalizedGroup
                });
                result[key] = restoredId;
            }
        }

        return result;
    }

    private static Order RequireSourceOrder(IDataStore store, long sourceOrderId)
    {
        var source = store.GetOrder(sourceOrderId)
                     ?? throw new InvalidOperationException(
                         "Нельзя компенсировать produced coverage: исходный INTERNAL заказ не найден.");
        if (source.Type != OrderType.Internal)
        {
            throw new InvalidOperationException(
                "Нельзя компенсировать produced coverage: provenance source больше не является INTERNAL.");
        }

        if (source.Status == OrderStatus.Cancelled)
        {
            throw new InvalidOperationException(
                "Нельзя компенсировать produced coverage в отменённый INTERNAL заказ.");
        }

        return source;
    }

    private static void EnsureSourceLineMatchesProvenance(
        OrderLine current,
        OrderCoverageTransferLine provenance,
        ProductionLinePurpose purpose,
        string? normalizedGroup)
    {
        if (current.ItemId != provenance.ItemId
            || current.ProductionPurpose != purpose
            || !string.Equals(
                NormalizeGroup(current.ProductionPalletGroup),
                normalizedGroup,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Нельзя восстановить INTERNAL demand автоматически: исходная строка изменила semantic attributes после adoption.");
        }
    }

    private static bool IsUncompensatedAdoption(OrderCoverageTransfer transfer)
    {
        return string.Equals(
                   transfer.TransferType,
                   OrderCoverageTransferType.PlannedPalletAdoption,
                   StringComparison.Ordinal)
               && string.IsNullOrWhiteSpace(transfer.CompensationKind)
               && !transfer.CompensatedAt.HasValue;
    }

    private static string? NormalizeGroup(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
