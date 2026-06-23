using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderHuBindingApplyFinalService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;
    private const string ReplacedByReadyHuReason = "replaced_by_ready_hu";

    private readonly IDataStore _dataStore;

    public OrderHuBindingApplyFinalService(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public OrderHuBindingApplyFinalResult ApplyFinal(long customerOrderId, OrderHuBindingApplyFinalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        OrderHuBindingApplyFinalResult? result = null;
        _dataStore.ExecuteInTransaction(store =>
        {
            result = ApplyFinalCore(store, customerOrderId, request);
        });

        return result ?? ApplyFinalCore(_dataStore, customerOrderId, request);
    }

    private OrderHuBindingApplyFinalResult ApplyFinalCore(
        IDataStore store,
        long customerOrderId,
        OrderHuBindingApplyFinalRequest request)
    {
        HuBindingApplyShared.ValidateRequestShape(request);

        if (customerOrderId <= 0)
        {
            throw Error("ORDER_NOT_FOUND", "Заказ не найден.");
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            throw new InvalidOperationException("Хранилище не поддерживает read-model кандидатов HU.");
        }

        var order = store.GetOrder(customerOrderId)
                    ?? throw Error("ORDER_NOT_FOUND", $"Заказ {customerOrderId} не найден.");
        if (order.Type != OrderType.Customer)
        {
            throw Error("ORDER_NOT_CUSTOMER", "Привязка HU доступна только для клиентского заказа.");
        }

        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw Error("ORDER_CLOSED", "Нельзя изменять HU для закрытого или отменённого заказа.");
        }

        var orderLines = store.GetOrderLines(customerOrderId)
            .Where(line => line.Id > 0)
            .ToDictionary(line => line.Id);
        if (orderLines.Count == 0)
        {
            throw Error("INVALID_REQUEST", "У заказа нет строк для применения HU.");
        }

        var existingPlanLines = store.GetOrderReceiptPlanLines(customerOrderId)
            .Where(line => line.QtyPlanned > QtyTolerance)
            .ToArray();
        var shipmentRemainingByLine = store.GetOrderShipmentRemaining(customerOrderId)
            .ToDictionary(line => line.OrderLineId);
        var reservedByOtherActiveCustomerOrders = store.GetReservedOrderReceiptHuCodes(customerOrderId)
            .Select(HuBindingApplyShared.NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedOwnerByHu = (store.GetHuOrderContextRows() ?? Array.Empty<HuOrderContextRow>())
            .Where(row => row.ReservedCustomerOrderId.HasValue && row.ReservedCustomerOrderId.Value != customerOrderId)
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => HuBindingApplyShared.NormalizeHu(row.HuCode)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var affectedLineIds = request.Lines.Select(line => line.OrderLineId).ToHashSet();
        var duplicateHuGuard = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var candidatesService = new HuReservationCandidatesService(store);
        var replacementLines = new List<OrderReceiptPlanLine>();
        var appliedLines = new List<OrderHuBindingApplyFinalLineResult>();
        var restoreLineIds = new List<(long OrderLineId, double ActivePlannedBefore)>();
        var palletsToCancel = new List<long>();

        foreach (var requestLine in request.Lines)
        {
            if (!orderLines.TryGetValue(requestLine.OrderLineId, out var orderLine))
            {
                throw Error(
                    "ORDER_LINE_NOT_FOUND",
                    $"Строка {requestLine.OrderLineId} не принадлежит заказу {customerOrderId}.");
            }

            var currentPlanForLine = existingPlanLines
                .Where(line => line.OrderLineId == orderLine.Id)
                .OrderBy(line => line.SortOrder)
                .ThenBy(line => line.Id)
                .ToArray();
            var previousHuCodes = currentPlanForLine
                .Select(line => HuBindingApplyShared.NormalizeHu(line.ToHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToArray();
            var previousHuSet = previousHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedHuSet = HuBindingApplyShared.NormalizeHuSet(requestLine.ExpectedBoundHuCodes);
            if (!previousHuSet.SetEquals(expectedHuSet))
            {
                throw Error(
                    "HU_BINDING_STALE",
                    "Список привязанных HU изменился. Обновите заказ и повторите действие.",
                    [
                        $"order_line_id={orderLine.Id}",
                        $"expected={string.Join(", ", expectedHuSet.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))}",
                        $"actual={string.Join(", ", previousHuSet.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))}"
                    ]);
            }

            var finalHuCodes = HuBindingApplyShared.NormalizeHuList(requestLine.FinalHuCodes);
            HuBindingApplyShared.ValidateDuplicateHuInFinalSelection(finalHuCodes, orderLine.Id, duplicateHuGuard);
            HuBindingApplyShared.ValidateHuNotReservedOnOtherUnaffectedLine(
                customerOrderId,
                orderLine.Id,
                finalHuCodes,
                affectedLineIds,
                existingPlanLines);

            var candidatesByHu = HuBindingApplyShared.BuildCandidatesByHu(candidatesService, customerOrderId, orderLine);
            var finalCandidates = new List<HuReservationCandidateResult>();
            foreach (var huCode in finalHuCodes)
            {
                if (reservedByOtherActiveCustomerOrders.Contains(huCode))
                {
                    if (reservedOwnerByHu.TryGetValue(huCode, out var owner))
                    {
                        throw Error(
                            "HU_RESERVED_BY_OTHER_ORDER",
                            $"HU '{huCode}' уже закреплён за другим активным клиентским заказом.",
                            [$"HU '{huCode}' принадлежит заказу {owner.ReservedCustomerOrderRef ?? owner.ReservedCustomerOrderId!.Value.ToString()}."]);
                    }

                    throw Error(
                        "HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' уже зарезервирован другим активным клиентским заказом.",
                        [$"HU '{huCode}' не может быть выбран для заказа {customerOrderId}."]);
                }

                if (!candidatesByHu.TryGetValue(huCode, out var candidate))
                {
                    throw Error(
                        "HU_NOT_AVAILABLE",
                        $"HU '{huCode}' недоступен для строки {orderLine.Id}.",
                        [$"HU '{huCode}' отсутствует среди доступных кандидатов или изменил статус."]);
                }

                if (candidate.ReservedByOrderId.HasValue && candidate.ReservedByOrderId.Value != customerOrderId)
                {
                    throw Error(
                        "HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' уже зарезервирован другим клиентским заказом.",
                        [$"HU '{huCode}' зарезервирован заказом {candidate.ReservedByOrderRef ?? candidate.ReservedByOrderId.ToString()}."]);
                }

                if (!string.Equals(candidate.Source, OrderHuReservationApplyService.SourceLedgerStock, StringComparison.OrdinalIgnoreCase)
                    || candidate.Qty <= QtyTolerance)
                {
                    throw Error(
                        "HU_NOT_AVAILABLE",
                        $"HU '{huCode}' недоступен для строки {orderLine.Id}.",
                        [$"HU '{huCode}': source={candidate.Source}, qty={candidate.Qty:0.###}."]);
                }

                finalCandidates.Add(candidate);
            }

            var finalBoundQty = finalCandidates.Sum(candidate => Math.Max(0, candidate.Qty));
            var previousBoundQty = currentPlanForLine.Sum(line => Math.Max(0, line.QtyPlanned));
            var remainingQty = HuBindingApplyShared.ResolveShipmentRemaining(orderLine, shipmentRemainingByLine);
            if (finalBoundQty > remainingQty + QtyTolerance)
            {
                throw Error(
                    "HU_QTY_EXCEEDS_REMAINING",
                    "Итоговая привязка HU больше остатка строки заказа.",
                    [
                        $"order_line_id={orderLine.Id}",
                        $"final_bound_qty={finalBoundQty:0.###}",
                        $"remaining_qty={remainingQty:0.###}"
                    ]);
            }

            var coverageAddedQty = Math.Max(0, finalBoundQty - previousBoundQty);
            var cancelledPalletIds = coverageAddedQty > QtyTolerance
                ? HuBindingApplyShared.SelectSafeWholePlannedPalletsToCancel(store, customerOrderId, orderLine, coverageAddedQty)
                : Array.Empty<long>();
            palletsToCancel.AddRange(cancelledPalletIds);

            if (previousBoundQty > finalBoundQty + QtyTolerance)
            {
                restoreLineIds.Add((orderLine.Id, HuBindingApplyShared.SumActiveProductionPalletQty(store, customerOrderId, orderLine.Id)));
            }

            replacementLines.AddRange(HuBindingApplyShared.BuildReplacementPlanLines(customerOrderId, orderLine, finalCandidates));

            var finalSet = finalHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            appliedLines.Add(new OrderHuBindingApplyFinalLineResult
            {
                OrderLineId = orderLine.Id,
                ItemId = orderLine.ItemId,
                PreviousHuCodes = previousHuCodes,
                FinalHuCodes = finalHuCodes,
                BoundHuCodes = finalHuCodes.Where(code => !previousHuSet.Contains(code)).ToArray(),
                DetachedHuCodes = previousHuCodes.Where(code => !finalSet.Contains(code)).ToArray(),
                ReservedQty = finalBoundQty,
                CancelledPlannedPalletCount = cancelledPalletIds.Length
            });
        }

        store.ReplaceOrderReceiptPlanLinesForOrderLines(customerOrderId, affectedLineIds, replacementLines);
        if (replacementLines.Any(line => line.QtyPlanned > QtyTolerance) && !order.UseReservedStock)
        {
            store.UpdateOrder(HuBindingApplyShared.CopyOrderWithReservedStock(order));
        }

        if (palletsToCancel.Count > 0)
        {
            var cancelled = store.CancelProductionPalletsForReadyHuBinding(
                palletsToCancel,
                ReplacedByReadyHuReason,
                DateTime.UtcNow);
            if (cancelled != palletsToCancel.Distinct().Count())
            {
                throw Error(
                    "HU_BINDING_PLAN_CONFLICT",
                    "Плановые паллеты изменились и не могут быть безопасно отменены.");
            }

            store.RemoveDocLinesForProductionPallets(palletsToCancel);
        }

        var restoredByLine = new Dictionary<long, double>();
        foreach (var restore in restoreLineIds)
        {
            var orderLine = orderLines[restore.OrderLineId];
            HuBindingApplyShared.RestoreProductionPlanForOrderLine(
                store,
                customerOrderId,
                restore.OrderLineId,
                orderLine.QtyOrdered);
            var activeAfter = HuBindingApplyShared.SumActiveProductionPalletQty(store, customerOrderId, restore.OrderLineId);
            restoredByLine[restore.OrderLineId] = Math.Max(0, activeAfter - restore.ActivePlannedBefore);
        }

        new OrderService(store).RefreshPersistedStatus(customerOrderId);

        return new OrderHuBindingApplyFinalResult
        {
            Ok = true,
            OrderId = customerOrderId,
            AppliedLines = appliedLines
                .Select(line => restoredByLine.TryGetValue(line.OrderLineId, out var restored)
                    ? new OrderHuBindingApplyFinalLineResult
                    {
                        OrderLineId = line.OrderLineId,
                        ItemId = line.ItemId,
                        PreviousHuCodes = line.PreviousHuCodes,
                        FinalHuCodes = line.FinalHuCodes,
                        BoundHuCodes = line.BoundHuCodes,
                        DetachedHuCodes = line.DetachedHuCodes,
                        ReservedQty = line.ReservedQty,
                        CancelledPlannedPalletCount = line.CancelledPlannedPalletCount,
                        RestoredPlannedQty = restored
                    }
                    : line)
                .ToArray()
        };
    }

    private static OrderHuBindingApplyFinalException Error(
        string code,
        string message,
        IReadOnlyList<string>? problems = null) =>
        HuBindingApplyShared.Error(code, message, problems);
}
