using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Атомарное применение окончательного набора привязок готовых HU сразу по нескольким
/// клиентским заказам. Вся работа выполняется в одной внешней транзакции
/// (<see cref="IDataStore.ExecuteInTransaction"/>): сначала строится immutable snapshot и
/// глобальное конечное состояние, затем — единственная запись через
/// <see cref="IDataStore.ReplaceOrderReceiptPlanLinesBatch"/> и корректировка производственных
/// планов как дельты original→final. Любая ошибка откатывает весь batch.
/// Пер-строчные алгоритмы переиспользуются из <see cref="HuBindingApplyShared"/>;
/// полный order-scoped <see cref="OrderHuBindingApplyFinalService.ApplyFinal"/> в цикле не вызывается.
/// </summary>
public sealed class OrderHuBindingManageApplyService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;
    private const string ReplacedByReadyHuReason = "replaced_by_ready_hu";

    private readonly IDataStore _dataStore;

    public OrderHuBindingManageApplyService(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public OrderHuBindingManageApplyResult ApplyFinal(OrderHuBindingManageApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        OrderHuBindingManageApplyResult? result = null;
        _dataStore.ExecuteInTransaction(store =>
        {
            result = ApplyFinalCore(store, request);
        });

        return result ?? ApplyFinalCore(_dataStore, request);
    }

    private OrderHuBindingManageApplyResult ApplyFinalCore(IDataStore store, OrderHuBindingManageApplyRequest request)
    {
        ValidateRequestShape(request);

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            throw new InvalidOperationException("Хранилище не поддерживает read-model кандидатов HU.");
        }

        var snapshot = LoadSnapshot(store, request);

        ValidateExpectedHuStates(request, snapshot);

        var scopeSet = request.Lines
            .Select(line => new OrderReceiptPlanLineKey(line.OrderId, line.OrderLineId))
            .ToHashSet();

        var replacementLines = new List<OrderReceiptPlanLine>();
        var perLine = new List<LineComputation>();
        var duplicateHuGuard = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Полная валидация и построение глобального конечного состояния ДО первой записи.
        foreach (var lineRequest in request.Lines)
        {
            perLine.Add(BuildLineComputation(snapshot, scopeSet, duplicateHuGuard, lineRequest, replacementLines));
        }

        // Единственная запись по всем затронутым строкам (delete-all-then-insert поддерживает циклы).
        store.ReplaceOrderReceiptPlanLinesBatch(scopeSet, replacementLines);

        // UseReservedStock ставится по существующей семантике и никогда не снимается.
        foreach (var orderId in snapshot.AffectedOrderIds)
        {
            var order = snapshot.Orders[orderId];
            var hasReserved = replacementLines.Any(line => line.OrderId == orderId && line.QtyPlanned > QtyTolerance);
            if (hasReserved && !order.UseReservedStock)
            {
                store.UpdateOrder(HuBindingApplyShared.CopyOrderWithReservedStock(order));
            }
        }

        // Производственный план: отменяется только реальный surplus покрытия (дедуп по HU), исключая FILLED/partial.
        var cancelledCountByLine = new Dictionary<long, int>();
        var palletsToCancel = new List<long>();
        foreach (var line in perLine)
        {
            var finalBoundQtyByHu = replacementLines
                .Where(plan => plan.OrderId == line.OrderId
                               && plan.OrderLineId == line.OrderLine.Id
                               && !string.IsNullOrWhiteSpace(plan.ToHu))
                .GroupBy(plan => plan.ToHu!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(plan => Math.Max(0, plan.QtyPlanned)), StringComparer.OrdinalIgnoreCase);
            var surplus = HuBindingApplyShared.ComputeCancellableFuturePlanSurplus(
                store, line.OrderId, line.OrderLine, finalBoundQtyByHu);
            var cancelledPalletIds = HuBindingApplyShared.SelectFuturePlanPalletsToCancel(
                store, line.OrderId, line.OrderLine, surplus);
            if (cancelledPalletIds.Length > 0)
            {
                palletsToCancel.AddRange(cancelledPalletIds);
                cancelledCountByLine[line.OrderLine.Id] = cancelledPalletIds.Length;
            }
        }

        if (palletsToCancel.Count > 0)
        {
            var cancelled = store.CancelProductionPalletsForReadyHuBinding(
                palletsToCancel, ReplacedByReadyHuReason, DateTime.UtcNow);
            if (cancelled != palletsToCancel.Distinct().Count())
            {
                throw Error("HU_BINDING_PLAN_CONFLICT", "Плановые паллеты изменились и не могут быть безопасно отменены.");
            }

            store.RemoveDocLinesForProductionPallets(palletsToCancel);
        }

        var restoredByLine = new Dictionary<long, double>();

        // Статус каждого затронутого заказа пересчитывается один раз.
        foreach (var orderId in snapshot.AffectedOrderIds)
        {
            new OrderService(store).RefreshPersistedStatus(orderId);
        }

        return BuildResult(perLine, cancelledCountByLine, restoredByLine);
    }

    private static Snapshot LoadSnapshot(IDataStore store, OrderHuBindingManageApplyRequest request)
    {
        var affectedOrderIds = request.Lines.Select(line => line.OrderId).Distinct().ToArray();

        var orders = new Dictionary<long, Order>();
        var orderLinesByOrder = new Dictionary<long, Dictionary<long, OrderLine>>();
        var planLinesByOrder = new Dictionary<long, OrderReceiptPlanLine[]>();
        var shipmentByOrder = new Dictionary<long, Dictionary<long, OrderShipmentLine>>();

        foreach (var orderId in affectedOrderIds)
        {
            var order = store.GetOrder(orderId)
                        ?? throw Error("ORDER_NOT_FOUND", $"Заказ {orderId} не найден.");
            if (order.Type != OrderType.Customer)
            {
                throw Error("ORDER_NOT_CUSTOMER", $"Заказ {orderId}: привязка HU доступна только для клиентского заказа.");
            }

            if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
            {
                throw Error("ORDER_CLOSED", $"Заказ {orderId} закрыт или отменён — изменение HU запрещено.");
            }

            orders[orderId] = order;
            orderLinesByOrder[orderId] = store.GetOrderLines(orderId)
                .Where(line => line.Id > 0)
                .ToDictionary(line => line.Id);
            planLinesByOrder[orderId] = store.GetOrderReceiptPlanLines(orderId)
                .Where(line => line.QtyPlanned > QtyTolerance)
                .ToArray();
            shipmentByOrder[orderId] = store.GetOrderShipmentRemaining(orderId)
                .ToDictionary(line => line.OrderLineId);
        }

        var huStockRows = store.GetHuStockRows() ?? Array.Empty<HuStockRow>();
        var qtyByHuItem = huStockRows
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => (Hu: HuBindingApplyShared.NormalizeHu(row.HuCode)!, row.ItemId))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
        var itemsByHu = huStockRows
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => HuBindingApplyShared.NormalizeHu(row.HuCode)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Where(row => row.Qty > QtyTolerance).Select(row => row.ItemId).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var globalOwnerOrderByHu = (store.GetHuOrderContextRows() ?? Array.Empty<HuOrderContextRow>())
            .Where(row => row.ReservedCustomerOrderId.HasValue && !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => HuBindingApplyShared.NormalizeHu(row.HuCode)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var globalReservedHu = (store.GetReservedOrderReceiptHuCodes(null) ?? Array.Empty<string>())
            .Select(HuBindingApplyShared.NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Текущий владелец (заказ + строка) для HU, привязанных в затронутых заказах.
        var currentOwnerByHu = new Dictionary<string, OrderReceiptPlanLineKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var orderId in affectedOrderIds)
        {
            foreach (var planLine in planLinesByOrder[orderId])
            {
                var huCode = HuBindingApplyShared.NormalizeHu(planLine.ToHu);
                if (!string.IsNullOrWhiteSpace(huCode))
                {
                    currentOwnerByHu[huCode!] = new OrderReceiptPlanLineKey(orderId, planLine.OrderLineId);
                }
            }
        }

        return new Snapshot(
            affectedOrderIds,
            orders,
            orderLinesByOrder,
            planLinesByOrder,
            shipmentByOrder,
            qtyByHuItem,
            itemsByHu,
            globalOwnerOrderByHu,
            globalReservedHu,
            currentOwnerByHu)
        {
            Store = store
        };
    }

    private static void ValidateExpectedHuStates(OrderHuBindingManageApplyRequest request, Snapshot snapshot)
    {
        foreach (var expected in request.ExpectedHuStates)
        {
            var huCode = HuBindingApplyShared.NormalizeHu(expected.HuCode)
                         ?? throw Error("INVALID_REQUEST", "Пустой hu_code в expected_hu_states.");

            if (IsMixed(huCode, snapshot))
            {
                throw Error("HU_MIXED_NOT_SUPPORTED",
                    $"HU '{huCode}' содержит несколько товаров и не поддерживается экраном управления.");
            }

            var stockQty = snapshot.QtyByHuItem.TryGetValue((huCode, expected.ItemId), out var qty) ? qty : 0d;
            if (stockQty <= QtyTolerance)
            {
                throw Error("HU_NOT_AVAILABLE",
                    $"HU '{huCode}' отсутствует на складе для товара {expected.ItemId}.",
                    [$"hu={huCode}", $"item_id={expected.ItemId}"]);
            }

            if (Math.Abs(stockQty - expected.ExpectedQty) > QtyTolerance)
            {
                throw Error("HU_QTY_CHANGED",
                    $"Количество HU '{huCode}' изменилось.",
                    [$"expected_qty={expected.ExpectedQty:0.###}", $"actual_qty={stockQty:0.###}"]);
            }

            var (actualOrderId, actualOrderLineId, actualReserved) = ResolveActualOwner(huCode, snapshot);
            var expectedFree = !expected.ExpectedOrderId.HasValue && !expected.ExpectedOrderLineId.HasValue;
            if (expectedFree != !actualReserved)
            {
                throw Error("HU_OWNER_CHANGED",
                    $"Владелец HU '{huCode}' изменился.",
                    OwnerProblems(expected, actualOrderId, actualOrderLineId, actualReserved));
            }

            if (!expectedFree && actualReserved)
            {
                if (actualOrderId.HasValue && expected.ExpectedOrderId != actualOrderId)
                {
                    throw Error("HU_OWNER_CHANGED",
                        $"Владелец HU '{huCode}' изменился.",
                        OwnerProblems(expected, actualOrderId, actualOrderLineId, actualReserved));
                }

                if (actualOrderLineId.HasValue
                    && expected.ExpectedOrderLineId.HasValue
                    && expected.ExpectedOrderLineId != actualOrderLineId)
                {
                    throw Error("HU_OWNER_CHANGED",
                        $"Строка-владелец HU '{huCode}' изменилась.",
                        OwnerProblems(expected, actualOrderId, actualOrderLineId, actualReserved));
                }
            }
        }
    }

    private static LineComputation BuildLineComputation(
        Snapshot snapshot,
        IReadOnlySet<OrderReceiptPlanLineKey> scopeSet,
        IDictionary<string, long> duplicateHuGuard,
        OrderHuBindingManageApplyLineRequest lineRequest,
        List<OrderReceiptPlanLine> replacementLines)
    {
        var orderId = lineRequest.OrderId;
        var orderLines = snapshot.OrderLinesByOrder[orderId];
        if (!orderLines.TryGetValue(lineRequest.OrderLineId, out var orderLine))
        {
            throw Error("ORDER_LINE_NOT_FOUND",
                $"Строка {lineRequest.OrderLineId} не принадлежит заказу {orderId}.");
        }

        var currentPlanForLine = snapshot.PlanLinesByOrder[orderId]
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
        var expectedHuSet = HuBindingApplyShared.NormalizeHuSet(lineRequest.ExpectedBoundHuCodes);
        if (!previousHuSet.SetEquals(expectedHuSet))
        {
            throw Error("HU_BINDING_STALE",
                "Список привязанных HU строки изменился. Обновите данные и повторите действие.",
                [
                    $"order_line_id={orderLine.Id}",
                    $"expected={string.Join(", ", expectedHuSet.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))}",
                    $"actual={string.Join(", ", previousHuSet.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))}"
                ]);
        }

        var finalHuCodes = HuBindingApplyShared.NormalizeHuList(lineRequest.FinalHuCodes);
        // Дубликат HU в итоговом состоянии (по всем строкам всех заказов: order_line_id глобально уникален).
        HuBindingApplyShared.ValidateDuplicateHuInFinalSelection(finalHuCodes, orderLine.Id, duplicateHuGuard);

        var finalCandidates = new List<(string HuCode, double Qty)>();
        foreach (var huCode in finalHuCodes)
        {
            if (IsMixed(huCode, snapshot))
            {
                throw Error("HU_MIXED_NOT_SUPPORTED",
                    $"HU '{huCode}' содержит несколько товаров и не поддерживается экраном управления.");
            }

            var stockQty = snapshot.QtyByHuItem.TryGetValue((huCode, orderLine.ItemId), out var qty) ? qty : 0d;
            if (stockQty <= QtyTolerance)
            {
                if (snapshot.ItemsByHu.TryGetValue(huCode, out var items) && items.Length > 0)
                {
                    throw Error("HU_ITEM_MISMATCH",
                        $"HU '{huCode}' содержит товар, отличный от строки {orderLine.Id}.",
                        [$"line_item_id={orderLine.ItemId}", $"hu_items={string.Join(", ", items)}"]);
                }

                throw Error("HU_NOT_AVAILABLE",
                    $"HU '{huCode}' отсутствует на складе или не имеет положительного остатка.",
                    [$"hu={huCode}", $"item_id={orderLine.ItemId}"]);
            }

            // Захват чужого HU запрещён: владелец должен быть свободен или входить в batch scope.
            if (snapshot.CurrentOwnerByHu.TryGetValue(huCode, out var ownerKey))
            {
                if (!scopeSet.Contains(ownerKey))
                {
                    throw Error("HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' закреплён за строкой вне выбранных изменений.",
                        [$"owner_order_id={ownerKey.OrderId}", $"owner_order_line_id={ownerKey.OrderLineId}"]);
                }
            }
            else if (snapshot.GlobalReservedHu.Contains(huCode) || snapshot.GlobalOwnerOrderByHu.ContainsKey(huCode))
            {
                var ownerRef = snapshot.GlobalOwnerOrderByHu.TryGetValue(huCode, out var owner)
                    ? owner.ReservedCustomerOrderRef ?? owner.ReservedCustomerOrderId?.ToString()
                    : null;
                string[]? problems = ownerRef == null ? null : [$"owner_order={ownerRef}"];
                throw Error("HU_RESERVED_BY_OTHER_ORDER",
                    $"HU '{huCode}' уже зарезервирован другим активным клиентским заказом.",
                    problems);
            }

            finalCandidates.Add((huCode, stockQty));
        }

        var finalBoundQty = finalCandidates.Sum(candidate => Math.Max(0, candidate.Qty));
        var previousBoundQty = currentPlanForLine.Sum(line => Math.Max(0, line.QtyPlanned));
        var remainingQty = HuBindingApplyShared.ResolveShipmentRemaining(orderLine, snapshot.ShipmentByOrder[orderId]);
        if (finalBoundQty > remainingQty + QtyTolerance)
        {
            throw Error("HU_QTY_EXCEEDS_REMAINING",
                "Итоговая привязка HU больше остатка строки заказа.",
                [
                    $"order_line_id={orderLine.Id}",
                    $"final_bound_qty={finalBoundQty:0.###}",
                    $"remaining_qty={remainingQty:0.###}"
                ]);
        }

        for (var index = 0; index < finalCandidates.Count; index++)
        {
            replacementLines.Add(new OrderReceiptPlanLine
            {
                OrderId = orderId,
                OrderLineId = orderLine.Id,
                ItemId = orderLine.ItemId,
                QtyPlanned = finalCandidates[index].Qty,
                ToHu = finalCandidates[index].HuCode,
                SortOrder = index
            });
        }

        var delta = finalBoundQty - previousBoundQty;
        var activePlannedBefore = delta < -QtyTolerance
            ? HuBindingApplyShared.SumActiveProductionPalletQty(snapshot.Store, orderId, orderLine.Id)
            : 0d;

        return new LineComputation(
            orderId,
            orderLine,
            previousHuCodes,
            previousHuSet,
            finalHuCodes,
            finalBoundQty,
            delta,
            activePlannedBefore);
    }

    private static OrderHuBindingManageApplyResult BuildResult(
        IReadOnlyList<LineComputation> perLine,
        IReadOnlyDictionary<long, int> cancelledCountByLine,
        IReadOnlyDictionary<long, double> restoredByLine)
    {
        var orders = perLine
            .GroupBy(line => line.OrderId)
            .Select(group => new OrderHuBindingManageApplyOrderResult
            {
                OrderId = group.Key,
                AppliedLines = group.Select(line =>
                {
                    var finalSet = line.FinalHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return new OrderHuBindingApplyFinalLineResult
                    {
                        OrderLineId = line.OrderLine.Id,
                        ItemId = line.OrderLine.ItemId,
                        PreviousHuCodes = line.PreviousHuCodes,
                        FinalHuCodes = line.FinalHuCodes,
                        BoundHuCodes = line.FinalHuCodes.Where(code => !line.PreviousHuSet.Contains(code)).ToArray(),
                        DetachedHuCodes = line.PreviousHuCodes.Where(code => !finalSet.Contains(code)).ToArray(),
                        ReservedQty = line.FinalBoundQty,
                        CancelledPlannedPalletCount = cancelledCountByLine.TryGetValue(line.OrderLine.Id, out var cancelled) ? cancelled : 0,
                        RestoredPlannedQty = restoredByLine.TryGetValue(line.OrderLine.Id, out var restored) ? restored : 0
                    };
                }).ToArray()
            })
            .ToArray();

        return new OrderHuBindingManageApplyResult
        {
            Ok = true,
            Orders = orders
        };
    }

    private static (long? OrderId, long? OrderLineId, bool Reserved) ResolveActualOwner(string huCode, Snapshot snapshot)
    {
        if (snapshot.CurrentOwnerByHu.TryGetValue(huCode, out var ownerKey))
        {
            return (ownerKey.OrderId, ownerKey.OrderLineId, true);
        }

        if (snapshot.GlobalOwnerOrderByHu.TryGetValue(huCode, out var context))
        {
            return (context.ReservedCustomerOrderId, null, true);
        }

        if (snapshot.GlobalReservedHu.Contains(huCode))
        {
            return (null, null, true);
        }

        return (null, null, false);
    }

    private static string[] OwnerProblems(ManageExpectedHuState expected, long? actualOrderId, long? actualOrderLineId, bool actualReserved)
    {
        return
        [
            $"expected_order_id={expected.ExpectedOrderId?.ToString() ?? "(free)"}",
            $"expected_order_line_id={expected.ExpectedOrderLineId?.ToString() ?? "(free)"}",
            $"actual_order_id={(actualReserved ? actualOrderId?.ToString() ?? "(unknown)" : "(free)")}",
            $"actual_order_line_id={(actualReserved ? actualOrderLineId?.ToString() ?? "(unknown)" : "(free)")}"
        ];
    }

    private static bool IsMixed(string huCode, Snapshot snapshot) =>
        snapshot.ItemsByHu.TryGetValue(huCode, out var items) && items.Length > 1;

    private static void ValidateRequestShape(OrderHuBindingManageApplyRequest request)
    {
        if (!string.Equals(request.Mode, OrderHuBindingManageApplyRequest.ReplaceFinalSelectionMode, StringComparison.Ordinal))
        {
            throw Error("INVALID_REQUEST", "Некорректный режим применения HU.");
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            throw Error("INVALID_REQUEST", "Не переданы строки для применения HU.");
        }

        var pairs = new HashSet<OrderReceiptPlanLineKey>();
        foreach (var line in request.Lines)
        {
            if (line.OrderId <= 0)
            {
                throw Error("ORDER_NOT_FOUND", "Заказ не указан.");
            }

            if (line.OrderLineId <= 0)
            {
                throw Error("ORDER_LINE_NOT_FOUND", "Строка заказа не указана.");
            }

            if (!pairs.Add(new OrderReceiptPlanLineKey(line.OrderId, line.OrderLineId)))
            {
                throw Error("INVALID_REQUEST", $"Строка {line.OrderId}/{line.OrderLineId} передана более одного раза.");
            }

            if (line.ExpectedBoundHuCodes == null)
            {
                throw Error("INVALID_REQUEST", "Не передан expected_bound_hu_codes.");
            }

            if (line.FinalHuCodes == null)
            {
                throw Error("INVALID_REQUEST", "Не передан final_hu_codes.");
            }
        }

        foreach (var state in request.ExpectedHuStates ?? Array.Empty<ManageExpectedHuState>())
        {
            if (string.IsNullOrWhiteSpace(state.HuCode))
            {
                throw Error("INVALID_REQUEST", "Пустой hu_code в expected_hu_states.");
            }

            if (state.ItemId <= 0)
            {
                throw Error("INVALID_REQUEST", "Не указан item_id в expected_hu_states.");
            }
        }
    }

    private static OrderHuBindingApplyFinalException Error(
        string code,
        string message,
        IReadOnlyList<string>? problems = null) =>
        HuBindingApplyShared.Error(code, message, problems);

    private sealed record LineComputation(
        long OrderId,
        OrderLine OrderLine,
        IReadOnlyList<string> PreviousHuCodes,
        IReadOnlySet<string> PreviousHuSet,
        IReadOnlyList<string> FinalHuCodes,
        double FinalBoundQty,
        double Delta,
        double ActivePlannedBefore);

    private sealed class Snapshot
    {
        public Snapshot(
            IReadOnlyList<long> affectedOrderIds,
            IReadOnlyDictionary<long, Order> orders,
            IReadOnlyDictionary<long, Dictionary<long, OrderLine>> orderLinesByOrder,
            IReadOnlyDictionary<long, OrderReceiptPlanLine[]> planLinesByOrder,
            IReadOnlyDictionary<long, Dictionary<long, OrderShipmentLine>> shipmentByOrder,
            IReadOnlyDictionary<(string HuCode, long ItemId), double> qtyByHuItem,
            IReadOnlyDictionary<string, long[]> itemsByHu,
            IReadOnlyDictionary<string, HuOrderContextRow> globalOwnerOrderByHu,
            IReadOnlySet<string> globalReservedHu,
            IReadOnlyDictionary<string, OrderReceiptPlanLineKey> currentOwnerByHu)
        {
            AffectedOrderIds = affectedOrderIds;
            Orders = orders;
            OrderLinesByOrder = orderLinesByOrder;
            PlanLinesByOrder = planLinesByOrder;
            ShipmentByOrder = shipmentByOrder;
            QtyByHuItem = qtyByHuItem;
            ItemsByHu = itemsByHu;
            GlobalOwnerOrderByHu = globalOwnerOrderByHu;
            GlobalReservedHu = globalReservedHu;
            CurrentOwnerByHu = currentOwnerByHu;
        }

        public IReadOnlyList<long> AffectedOrderIds { get; }
        public IReadOnlyDictionary<long, Order> Orders { get; }
        public IReadOnlyDictionary<long, Dictionary<long, OrderLine>> OrderLinesByOrder { get; }
        public IReadOnlyDictionary<long, OrderReceiptPlanLine[]> PlanLinesByOrder { get; }
        public IReadOnlyDictionary<long, Dictionary<long, OrderShipmentLine>> ShipmentByOrder { get; }
        public IReadOnlyDictionary<(string HuCode, long ItemId), double> QtyByHuItem { get; }
        public IReadOnlyDictionary<string, long[]> ItemsByHu { get; }
        public IReadOnlyDictionary<string, HuOrderContextRow> GlobalOwnerOrderByHu { get; }
        public IReadOnlySet<string> GlobalReservedHu { get; }
        public IReadOnlyDictionary<string, OrderReceiptPlanLineKey> CurrentOwnerByHu { get; }

        // Производственные паллеты читаются напрямую из стора во время расчёта дельты плана.
        public IDataStore Store { get; init; } = null!;
    }
}
