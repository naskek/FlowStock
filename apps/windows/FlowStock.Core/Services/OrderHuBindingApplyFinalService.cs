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
        ValidateRequestShape(request);

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
            .Select(NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedOwnerByHu = (store.GetHuOrderContextRows() ?? Array.Empty<HuOrderContextRow>())
            .Where(row => row.ReservedCustomerOrderId.HasValue && row.ReservedCustomerOrderId.Value != customerOrderId)
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => NormalizeHu(row.HuCode)!, StringComparer.OrdinalIgnoreCase)
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
                .Select(line => NormalizeHu(line.ToHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToArray();
            var previousHuSet = previousHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedHuSet = NormalizeHuSet(requestLine.ExpectedBoundHuCodes);
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

            var finalHuCodes = NormalizeHuList(requestLine.FinalHuCodes);
            ValidateDuplicateHuInFinalSelection(finalHuCodes, orderLine.Id, duplicateHuGuard);
            ValidateHuNotReservedOnOtherUnaffectedLine(
                customerOrderId,
                orderLine.Id,
                finalHuCodes,
                affectedLineIds,
                existingPlanLines);

            var candidatesByHu = BuildCandidatesByHu(candidatesService, customerOrderId, orderLine);
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
            var remainingQty = ResolveShipmentRemaining(orderLine, shipmentRemainingByLine);
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
                ? SelectSafeWholePlannedPalletsToCancel(store, customerOrderId, orderLine, coverageAddedQty)
                : Array.Empty<long>();
            palletsToCancel.AddRange(cancelledPalletIds);

            if (previousBoundQty > finalBoundQty + QtyTolerance)
            {
                restoreLineIds.Add((orderLine.Id, SumActiveProductionPalletQty(store, customerOrderId, orderLine.Id)));
            }

            for (var index = 0; index < finalCandidates.Count; index++)
            {
                var candidate = finalCandidates[index];
                replacementLines.Add(new OrderReceiptPlanLine
                {
                    OrderId = customerOrderId,
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyPlanned = candidate.Qty,
                    ToHu = candidate.HuCode,
                    SortOrder = index
                });
            }

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
            store.UpdateOrder(CopyOrderWithReservedStock(order));
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
            new ProductionPalletService(store).SyncOrderLinePlanInStore(
                store,
                customerOrderId,
                restore.OrderLineId,
                orderLine.QtyOrdered,
                oldOrderedQty: null,
                source: "HU_BINDING_DETACH");
            var activeAfter = SumActiveProductionPalletQty(store, customerOrderId, restore.OrderLineId);
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

    private static void ValidateRequestShape(OrderHuBindingApplyFinalRequest request)
    {
        if (!string.Equals(request.Mode, OrderHuBindingApplyFinalRequest.ReplaceFinalSelectionMode, StringComparison.Ordinal))
        {
            throw Error("INVALID_REQUEST", "Некорректный режим применения HU.");
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            throw Error("INVALID_REQUEST", "Не переданы строки для применения HU.");
        }

        var lineIds = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (line.OrderLineId <= 0)
            {
                throw Error("ORDER_LINE_NOT_FOUND", "Строка заказа не указана.");
            }

            if (!lineIds.Add(line.OrderLineId))
            {
                throw Error("INVALID_REQUEST", $"Строка {line.OrderLineId} передана более одного раза.");
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
    }

    private static Dictionary<string, HuReservationCandidateResult> BuildCandidatesByHu(
        HuReservationCandidatesService candidatesService,
        long customerOrderId,
        OrderLine orderLine)
    {
        var result = candidatesService.Build(new HuReservationCandidatesQuery
        {
            OrderId = customerOrderId,
            Lines =
            [
                new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = orderLine.Id.ToString(),
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyOrdered = orderLine.QtyOrdered
                }
            ],
            ExcludeHuCodes = Array.Empty<string>()
        });

        return result.Lines
            .SelectMany(line => line.Candidates)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.HuCode))
            .GroupBy(candidate => NormalizeHu(candidate.HuCode)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static long[] SelectSafeWholePlannedPalletsToCancel(
        IDataStore store,
        long customerOrderId,
        OrderLine orderLine,
        double qtyToCover)
    {
        var activePallets = GetActiveProductionPalletsForOrderLine(store, customerOrderId, orderLine.Id).ToArray();
        if (activePallets.Length == 0)
        {
            return [];
        }

        var safe = new List<(ProductionPallet Pallet, double Qty)>();
        foreach (var pallet in activePallets)
        {
            if (!IsSafeWholePlannedCustomerPallet(store, customerOrderId, orderLine, pallet, out var qty))
            {
                throw Error(
                    "HU_BINDING_PLAN_CONFLICT",
                    "Строка имеет производственные паллеты, которые нельзя безопасно заменить готовым HU.",
                    [$"production_pallet_id={pallet.Id}", $"status={pallet.Status}"]);
            }

            safe.Add((pallet, qty));
        }

        var selected = SelectExactPalletSubset(safe, qtyToCover);
        if (selected.Count == 0)
        {
            throw Error(
                "HU_BINDING_PLAN_CONFLICT",
                "Готовый HU не может заменить целые плановые паллеты без частичной отмены.",
                [$"order_line_id={orderLine.Id}", $"qty_to_cover={qtyToCover:0.###}"]);
        }

        return selected.Select(pallet => pallet.Id).ToArray();
    }

    private static bool IsSafeWholePlannedCustomerPallet(
        IDataStore store,
        long customerOrderId,
        OrderLine orderLine,
        ProductionPallet pallet,
        out double qty)
    {
        qty = ResolvePalletQtyForOrderLine(pallet, orderLine.Id);
        if (qty <= QtyTolerance)
        {
            return false;
        }

        if (pallet.OrderId != customerOrderId
            || !string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.Ordinal)
            || pallet.PrintedAt.HasValue
            || pallet.FilledAt.HasValue)
        {
            return false;
        }

        var lines = pallet.Lines ?? Array.Empty<ProductionPalletComponentLine>();
        if (lines.Count > 1)
        {
            return false;
        }

        if (lines.Any(line => line.FilledQty > QtyTolerance
                              || line.OrderLineId != orderLine.Id
                              || line.ItemId != orderLine.ItemId))
        {
            return false;
        }

        if (lines.Count == 0 && (pallet.OrderLineId != orderLine.Id || pallet.ItemId != orderLine.ItemId))
        {
            return false;
        }

        var docLineIds = new HashSet<long>();
        if (pallet.DocLineId > 0)
        {
            docLineIds.Add(pallet.DocLineId);
        }

        foreach (var line in lines.Where(line => line.DocLineId > 0))
        {
            docLineIds.Add(line.DocLineId);
        }

        return !store.HasUnsafeMarkingForProductionPalletReplacement(
            customerOrderId,
            orderLine.Id,
            orderLine.ItemId,
            docLineIds);
    }

    private static IReadOnlyList<ProductionPallet> GetActiveProductionPalletsForOrderLine(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.Ordinal))
            .Where(pallet => PalletAppliesToOrderLine(pallet, orderLineId))
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    private static double SumActiveProductionPalletQty(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        return GetActiveProductionPalletsForOrderLine(store, orderId, orderLineId)
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
    }

    private static bool PalletAppliesToOrderLine(ProductionPallet pallet, long orderLineId)
    {
        return pallet.OrderLineId == orderLineId
               || (pallet.Lines ?? Array.Empty<ProductionPalletComponentLine>())
               .Any(line => line.OrderLineId == orderLineId);
    }

    private static double ResolvePalletQtyForOrderLine(ProductionPallet pallet, long orderLineId)
    {
        var componentQty = (pallet.Lines ?? Array.Empty<ProductionPalletComponentLine>())
            .Where(line => line.OrderLineId == orderLineId)
            .Sum(line => Math.Max(0, line.PlannedQty));
        return componentQty > QtyTolerance ? componentQty : Math.Max(0, pallet.PlannedQty);
    }

    private static IReadOnlyList<ProductionPallet> SelectExactPalletSubset(
        IReadOnlyList<(ProductionPallet Pallet, double Qty)> pallets,
        double targetQty)
    {
        var targetUnits = ToQtyUnits(targetQty);
        if (targetUnits <= 0)
        {
            return [];
        }

        var bestByTotal = new Dictionary<long, List<ProductionPallet>>
        {
            [0] = []
        };
        foreach (var entry in pallets.OrderBy(entry => entry.Pallet.Id))
        {
            var units = ToQtyUnits(entry.Qty);
            if (units <= 0)
            {
                continue;
            }

            foreach (var snapshot in bestByTotal.ToArray())
            {
                var total = snapshot.Key + units;
                if (total > targetUnits || bestByTotal.ContainsKey(total))
                {
                    continue;
                }

                var selected = new List<ProductionPallet>(snapshot.Value) { entry.Pallet };
                bestByTotal[total] = selected;
            }
        }

        return bestByTotal.TryGetValue(targetUnits, out var exact)
            ? exact
            : Array.Empty<ProductionPallet>();
    }

    private static long ToQtyUnits(double qty) => (long)Math.Round(Math.Max(0, qty) * 1_000_000d);

    private static double ResolveShipmentRemaining(
        OrderLine orderLine,
        IReadOnlyDictionary<long, OrderShipmentLine> shipmentRemainingByLine)
    {
        return shipmentRemainingByLine.TryGetValue(orderLine.Id, out var shipmentLine)
            ? Math.Max(0, shipmentLine.QtyRemaining)
            : Math.Max(0, orderLine.QtyOrdered);
    }

    private static void ValidateDuplicateHuInFinalSelection(
        IReadOnlyList<string> finalHuCodes,
        long orderLineId,
        IDictionary<string, long> huToOrderLine)
    {
        foreach (var huCode in finalHuCodes)
        {
            if (huToOrderLine.TryGetValue(huCode, out var existingOrderLineId)
                && existingOrderLineId != orderLineId)
            {
                throw Error(
                    "DUPLICATE_HU_IN_REQUEST",
                    $"HU '{huCode}' выбран более одного раза в одном запросе.",
                    [$"HU '{huCode}': lines {existingOrderLineId} и {orderLineId}"]);
            }

            huToOrderLine[huCode] = orderLineId;
        }
    }

    private static void ValidateHuNotReservedOnOtherUnaffectedLine(
        long customerOrderId,
        long orderLineId,
        IReadOnlyList<string> finalHuCodes,
        IReadOnlySet<long> affectedOrderLineIds,
        IReadOnlyList<OrderReceiptPlanLine> existingPlanLines)
    {
        if (finalHuCodes.Count == 0)
        {
            return;
        }

        var finalSet = finalHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var planLine in existingPlanLines)
        {
            var huCode = NormalizeHu(planLine.ToHu);
            if (planLine.OrderId != customerOrderId
                || planLine.OrderLineId == orderLineId
                || affectedOrderLineIds.Contains(planLine.OrderLineId)
                || string.IsNullOrWhiteSpace(huCode)
                || !finalSet.Contains(huCode))
            {
                continue;
            }

            throw Error(
                "HU_RESERVED_BY_OTHER_ORDER",
                $"HU '{huCode}' уже зарезервирован другой строкой этого заказа.",
                [$"HU '{huCode}': order_line_id={planLine.OrderLineId}"]);
        }
    }

    private static HashSet<string> NormalizeHuSet(IReadOnlyList<string>? huCodes) =>
        NormalizeHuList(huCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> NormalizeHuList(IReadOnlyList<string>? huCodes)
    {
        if (huCodes == null || huCodes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var huCode in huCodes)
        {
            var normalized = NormalizeHu(huCode);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string? NormalizeHu(string? huCode)
    {
        return string.IsNullOrWhiteSpace(huCode)
            ? null
            : huCode.Trim().ToUpperInvariant();
    }

    private static OrderHuBindingApplyFinalException Error(
        string code,
        string message,
        IReadOnlyList<string>? problems = null) =>
        new(code, message, problems);

    private static Order CopyOrderWithReservedStock(Order order)
    {
        return new Order
        {
            Id = order.Id,
            OrderRef = order.OrderRef,
            Type = order.Type,
            PartnerId = order.PartnerId,
            DueDate = order.DueDate,
            Status = order.Status,
            Comment = order.Comment,
            CreatedAt = order.CreatedAt,
            ShippedAt = order.ShippedAt,
            PartnerName = order.PartnerName,
            PartnerCode = order.PartnerCode,
            UseReservedStock = true,
            MarkingStatus = order.MarkingStatus,
            IsLegacyExcelGeneratedMarkingStatus = order.IsLegacyExcelGeneratedMarkingStatus,
            MarkingRequired = order.MarkingRequired,
            MarkingApplies = order.MarkingApplies,
            MarkingCodeCovered = order.MarkingCodeCovered,
            MarkingExcelGeneratedAt = order.MarkingExcelGeneratedAt,
            MarkingPrintedAt = order.MarkingPrintedAt
        };
    }
}
