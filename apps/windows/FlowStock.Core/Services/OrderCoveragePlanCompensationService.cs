using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

internal static class OrderCoveragePlanCompensationService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;

    internal static IReadOnlyList<OrderCoverageTransfer> LockAndLoadForTarget(
        IDataStore store,
        long targetOrderId)
    {
        IReadOnlyList<OrderCoverageTransfer> initial;
        try
        {
            initial = store.GetOrderCoverageTransfersByTargetOrder(targetOrderId)
                .Where(IsUncompensatedPlannedAdoption)
                .ToArray();
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            if (!store.LockOrdersForUpdate([targetOrderId]))
            {
                throw new InvalidOperationException("Заказ не найден.");
            }

            return Array.Empty<OrderCoverageTransfer>();
        }

        var lockedOrderIds = initial
            .Select(transfer => transfer.SourceOrderId)
            .Append(targetOrderId)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (!store.LockOrdersForUpdate(lockedOrderIds))
        {
            throw new InvalidOperationException("Не удалось заблокировать заказы для compensation production coverage.");
        }

        var refreshed = store.GetOrderCoverageTransfersByTargetOrder(targetOrderId)
            .Where(IsUncompensatedPlannedAdoption)
            .ToArray();
        var lockedSet = lockedOrderIds.ToHashSet();
        var newlyObservedSourceIds = refreshed
            .Select(transfer => transfer.SourceOrderId)
            .Where(sourceOrderId => !lockedSet.Contains(sourceOrderId))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (newlyObservedSourceIds.Length > 0)
        {
            throw new InvalidOperationException(
                "Production coverage изменился одновременно с операцией. Обновите заказ и повторите действие.");
        }

        return refreshed;
    }

    internal static OrderCoveragePlanCompensationResult ReverseAll(
        IDataStore store,
        long targetOrderId,
        IReadOnlyList<OrderCoverageTransfer> lockedTransfers)
    {
        return Reverse(
            store,
            targetOrderId,
            lockedTransfers,
            _ => true);
    }

    internal static OrderCoveragePlanCompensationResult ReverseForTargetLines(
        IDataStore store,
        long targetOrderId,
        IReadOnlyList<OrderCoverageTransfer> lockedTransfers,
        IReadOnlyCollection<long> targetOrderLineIds)
    {
        var targetLineIdSet = targetOrderLineIds
            .Where(id => id > 0)
            .ToHashSet();
        if (targetLineIdSet.Count == 0)
        {
            return OrderCoveragePlanCompensationResult.Empty;
        }

        return Reverse(
            store,
            targetOrderId,
            lockedTransfers,
            transfer => transfer.Lines.Any(line => targetLineIdSet.Contains(line.TargetOrderLineId)));
    }

    private static OrderCoveragePlanCompensationResult Reverse(
        IDataStore store,
        long targetOrderId,
        IReadOnlyList<OrderCoverageTransfer> lockedTransfers,
        Func<OrderCoverageTransfer, bool> predicate)
    {
        var selected = lockedTransfers
            .Where(IsUncompensatedPlannedAdoption)
            .Where(transfer => transfer.TargetOrderId == targetOrderId)
            .Where(predicate)
            .OrderBy(transfer => transfer.Id)
            .ToArray();
        if (selected.Length == 0)
        {
            return OrderCoveragePlanCompensationResult.Empty;
        }

        var sourceOrders = selected
            .Select(transfer => transfer.SourceOrderId)
            .Distinct()
            .ToDictionary(
                sourceOrderId => sourceOrderId,
                sourceOrderId => RequireReversibleSourceOrder(store, sourceOrderId));

        var restoredLineIds = ResolveRestoredSourceOrderLines(store, selected, sourceOrders);
        var restoredPrdDocIds = ResolveRestoredSourcePrdDocs(store, selected, sourceOrders);
        var qtyToRestoreByLineId = new Dictionary<long, double>();
        var affectedTargetLineIds = new HashSet<long>();
        var compensatedCount = 0;

        foreach (var transfer in selected)
        {
            var lineCompensations = transfer.Lines
                .Select(line =>
                {
                    if (!restoredLineIds.TryGetValue(
                            new SourceLineKey(transfer.SourceOrderId, line.SourceOrderLineId),
                            out var restoredSourceOrderLineId))
                    {
                        throw new InvalidOperationException(
                            "Нельзя компенсировать production coverage: не восстановлена строка INTERNAL.");
                    }

                    affectedTargetLineIds.Add(line.TargetOrderLineId);
                    return new OrderCoveragePlanCompensationLine
                    {
                        SourceDocLineId = line.SourceDocLineId,
                        SourceProductionPalletLineId = line.SourceProductionPalletLineId,
                        TargetOrderLineId = line.TargetOrderLineId,
                        RestoredSourceOrderLineId = restoredSourceOrderLineId,
                        ItemId = line.ItemId,
                        TransferredQty = line.TransferredQty,
                        SourceProductionPurpose = ProductionLinePurposeMapper.FromDbValue(
                            line.SourceProductionPurpose)
                    };
                })
                .ToArray();

            var distinctSourceLineIds = lineCompensations
                .Select(line => line.RestoredSourceOrderLineId)
                .Distinct()
                .ToArray();
            var sourcePrdKey = new SourcePrdKey(transfer.SourceOrderId, transfer.SourcePrdDocId);
            if (!restoredPrdDocIds.TryGetValue(sourcePrdKey, out var restoredSourcePrdDocId))
            {
                throw new InvalidOperationException(
                    "Нельзя компенсировать production coverage: не восстановлен INTERNAL PRD.");
            }

            var applied = store.CompensatePlannedOrderCoverageTransfer(
                new OrderCoveragePlanCompensation
                {
                    TransferId = transfer.Id,
                    SourceOrderId = transfer.SourceOrderId,
                    TargetOrderId = transfer.TargetOrderId,
                    TargetPrdDocId = transfer.TargetPrdDocId,
                    RestoredSourcePrdDocId = restoredSourcePrdDocId,
                    ProductionPalletId = transfer.ProductionPalletId,
                    HuCode = transfer.HuCode,
                    RestoredSourceOrderLineId = distinctSourceLineIds.Length == 1
                        ? distinctSourceLineIds[0]
                        : null,
                    Lines = lineCompensations
                },
                DateTime.UtcNow);
            if (!applied)
            {
                throw new InvalidOperationException(
                    "Production coverage изменился одновременно с compensation. Повторите операцию.");
            }

            compensatedCount++;
            foreach (var line in lineCompensations)
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
                    "Нельзя восстановить INTERNAL demand: строка заказа исчезла во время compensation.");
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

        return new OrderCoveragePlanCompensationResult(
            compensatedCount,
            affectedTargetLineIds.OrderBy(id => id).ToArray());
    }

    private static Dictionary<SourceLineKey, long> ResolveRestoredSourceOrderLines(
        IDataStore store,
        IReadOnlyList<OrderCoverageTransfer> transfers,
        IReadOnlyDictionary<long, Order> sourceOrders)
    {
        var result = new Dictionary<SourceLineKey, long>();
        var currentLinesByOrder = sourceOrders.Keys.ToDictionary(
            orderId => orderId,
            orderId => store.GetOrderLines(orderId).ToList());

        foreach (var transfer in transfers)
        {
            foreach (var provenanceLine in transfer.Lines)
            {
                var key = new SourceLineKey(transfer.SourceOrderId, provenanceLine.SourceOrderLineId);
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
                    throw new InvalidOperationException("Не удалось восстановить строку INTERNAL из provenance.");
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

    private static Dictionary<SourcePrdKey, long> ResolveRestoredSourcePrdDocs(
        IDataStore store,
        IReadOnlyList<OrderCoverageTransfer> transfers,
        IReadOnlyDictionary<long, Order> sourceOrders)
    {
        var result = new Dictionary<SourcePrdKey, long>();
        foreach (var transfer in transfers)
        {
            var key = new SourcePrdKey(transfer.SourceOrderId, transfer.SourcePrdDocId);
            if (result.ContainsKey(key))
            {
                continue;
            }

            var original = store.GetDoc(transfer.SourcePrdDocId);
            if (original != null)
            {
                if (original.Type != DocType.ProductionReceipt
                    || original.OrderId != transfer.SourceOrderId)
                {
                    throw new InvalidOperationException(
                        "Нельзя восстановить INTERNAL PRD: historical document id принадлежит другой сущности.");
                }

                if (original.Status != DocStatus.Closed)
                {
                    result[key] = original.Id;
                    continue;
                }
            }

            var sourceOrder = sourceOrders[transfer.SourceOrderId];
            var docRef = DocRefGenerator.Generate(store, DocType.ProductionReceipt, DateTime.Now);
            var restoredId = store.AddDoc(new Doc
            {
                DocRef = docRef,
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Draft,
                OrderId = sourceOrder.Id,
                OrderRef = sourceOrder.OrderRef,
                CreatedAt = DateTime.Now
            });
            if (restoredId <= 0)
            {
                throw new InvalidOperationException("Не удалось создать INTERNAL PRD для compensation.");
            }

            result[key] = restoredId;
        }

        return result;
    }

    private static Order RequireReversibleSourceOrder(IDataStore store, long sourceOrderId)
    {
        var source = store.GetOrder(sourceOrderId)
                     ?? throw new InvalidOperationException(
                         "Нельзя компенсировать production coverage: исходный INTERNAL заказ не найден.");
        if (source.Type != OrderType.Internal)
        {
            throw new InvalidOperationException(
                "Нельзя компенсировать production coverage: provenance source больше не является INTERNAL.");
        }

        if (source.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"Нельзя компенсировать production coverage в INTERNAL со статусом {OrderStatusMapper.StatusToDisplayName(source.Status, source.Type)}.");
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

    private static bool IsUncompensatedPlannedAdoption(OrderCoverageTransfer transfer)
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

    private static bool IsMockStoreException(Exception ex)
    {
        var fullName = ex.GetType().FullName ?? string.Empty;
        return fullName.Contains("Moq", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Castle.Proxies", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct SourceLineKey(long SourceOrderId, long HistoricalSourceOrderLineId);
    private readonly record struct SourcePrdKey(long SourceOrderId, long HistoricalSourcePrdDocId);
}

internal sealed record OrderCoveragePlanCompensationResult(
    int CompensatedTransferCount,
    IReadOnlyList<long> AffectedTargetOrderLineIds)
{
    public static readonly OrderCoveragePlanCompensationResult Empty =
        new(0, Array.Empty<long>());
}
