using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderHuReservationApplyService
{
    public const string SourceLedgerStock = "LEDGER_STOCK";

    private readonly IDataStore _dataStore;

    public OrderHuReservationApplyService(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public OrderHuReservationApplyResult Apply(long customerOrderId, OrderHuReservationApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        OrderHuReservationApplyResult? result = null;
        _dataStore.ExecuteInTransaction(store =>
        {
            result = ApplyCore(store, customerOrderId, request);
        });

        return result ?? ApplyCore(_dataStore, customerOrderId, request);
    }

    private OrderHuReservationApplyResult ApplyCore(
        IDataStore store,
        long customerOrderId,
        OrderHuReservationApplyRequest request)
    {
        var candidatesService = new HuReservationCandidatesService(store);

        if (customerOrderId <= 0)
        {
            throw new OrderHuReservationApplyException(
                "ORDER_NOT_FOUND",
                "Заказ не найден.");
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            throw new OrderHuReservationApplyException(
                "INVALID_REQUEST",
                "Не переданы строки для применения HU.");
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            throw new InvalidOperationException("Хранилище не поддерживает read-model кандидатов HU.");
        }

        var order = store.GetOrder(customerOrderId);
        if (order == null)
        {
            throw new OrderHuReservationApplyException(
                "ORDER_NOT_FOUND",
                $"Заказ {customerOrderId} не найден.");
        }

        if (order.Type != OrderType.Customer)
        {
            throw new OrderHuReservationApplyException(
                "ORDER_NOT_CUSTOMER",
                "Резервирование HU доступно только для клиентского заказа.");
        }

        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new OrderHuReservationApplyException(
                "ORDER_CLOSED",
                "Нельзя изменять резервы для закрытого или отменённого заказа.");
        }

        var orderLines = store.GetOrderLines(customerOrderId)
            .Where(line => line.Id > 0)
            .ToDictionary(line => line.Id);
        if (orderLines.Count == 0)
        {
            throw new OrderHuReservationApplyException(
                "INVALID_REQUEST",
                "У заказа нет строк для применения HU.");
        }

        var shipmentRemainingByLine = store.GetOrderShipmentRemaining(customerOrderId)
            .ToDictionary(line => line.OrderLineId);
        var existingPlanLines = store.GetOrderReceiptPlanLines(customerOrderId);
        var reservedByOtherActiveCustomerOrders = store.GetReservedOrderReceiptHuCodes(customerOrderId)
            .Select(code => code.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedOwnerByHu = (store.GetHuOrderContextRows() ?? Array.Empty<HuOrderContextRow>())
            .Where(row => row.ReservedCustomerOrderId.HasValue && row.ReservedCustomerOrderId.Value != customerOrderId)
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => row.HuCode.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var affectedOrderLineIds = new HashSet<long>();
        var replacementPlanLines = new List<OrderReceiptPlanLine>();
        var appliedLines = new List<OrderHuReservationApplyLineResult>();
        var huToRequestLine = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var requestLine in request.Lines)
        {
            if (requestLine.OrderLineId <= 0)
            {
                throw new OrderHuReservationApplyException(
                    "ORDER_LINE_NOT_FOUND",
                    "Строка заказа не указана.");
            }

            if (!orderLines.TryGetValue(requestLine.OrderLineId, out var orderLine))
            {
                throw new OrderHuReservationApplyException(
                    "ORDER_LINE_NOT_FOUND",
                    $"Строка {requestLine.OrderLineId} не принадлежит заказу {customerOrderId}.");
            }

            affectedOrderLineIds.Add(orderLine.Id);
            var selectedHuCodes = NormalizeHuCodes(requestLine.SelectedHuCodes);
            ValidateDuplicateHuInRequest(selectedHuCodes, orderLine.Id, huToRequestLine);
            ValidateHuNotReservedOnOtherLinesOfSameOrder(
                customerOrderId,
                orderLine.Id,
                selectedHuCodes,
                affectedOrderLineIds,
                existingPlanLines);

            if (selectedHuCodes.Count == 0)
            {
                appliedLines.Add(BuildAppliedLineResult(orderLine, [], 0));
                continue;
            }

            if (!shipmentRemainingByLine.TryGetValue(orderLine.Id, out var shipmentLine))
            {
                shipmentLine = new OrderShipmentLine
                {
                    OrderLineId = orderLine.Id,
                    OrderId = customerOrderId,
                    ItemId = orderLine.ItemId,
                    QtyOrdered = orderLine.QtyOrdered,
                    QtyShipped = 0,
                    QtyRemaining = orderLine.QtyOrdered
                };
            }

            var remainingQty = Math.Max(0, shipmentLine.QtyRemaining);
            var candidatesByHu = BuildAvailableCandidatesByHu(
                candidatesService,
                customerOrderId,
                orderLine,
                shipmentLine.QtyOrdered);
            var selectedHu = new List<OrderHuReservationAppliedHuResult>();
            var reservedQty = 0d;
            var sortOrder = 0;

            foreach (var huCode in selectedHuCodes)
            {
                if (reservedByOtherActiveCustomerOrders.Contains(huCode))
                {
                    if (reservedOwnerByHu.TryGetValue(huCode, out var owner))
                    {
                        throw new OrderHuReservationApplyException(
                            "HU_RESERVED_BY_OTHER_ORDER",
                            $"HU '{huCode}' уже закреплён за другим активным клиентским заказом.",
                            [$"HU '{huCode}' принадлежит заказу {owner.ReservedCustomerOrderRef ?? owner.ReservedCustomerOrderId!.Value.ToString()}."]);
                    }

                    throw new OrderHuReservationApplyException(
                        "HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' уже зарезервирован другим активным клиентским заказом.",
                        [$"HU '{huCode}' не может быть выбран для заказа {customerOrderId}."]);
                }

                if (!candidatesByHu.TryGetValue(huCode, out var candidate))
                {
                    throw new OrderHuReservationApplyException(
                        "HU_NOT_AVAILABLE",
                        $"HU '{huCode}' недоступен для строки {orderLine.Id}.",
                        [$"HU '{huCode}' отсутствует среди доступных кандидатов или изменил статус."]);
                }

                if (candidate.ReservedByOrderId.HasValue
                    && candidate.ReservedByOrderId.Value != customerOrderId)
                {
                    throw new OrderHuReservationApplyException(
                        "HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' уже зарезервирован другим клиентским заказом.",
                        [
                            $"HU '{huCode}' зарезервирован заказом {candidate.ReservedByOrderRef ?? candidate.ReservedByOrderId.ToString()}."
                        ]);
                }

                if (!IsAllowedSource(candidate.Source))
                {
                    throw new OrderHuReservationApplyException(
                        "HU_NOT_AVAILABLE",
                        $"HU '{huCode}' имеет неподдерживаемый источник '{candidate.Source}'.",
                        [$"HU '{huCode}': source={candidate.Source}"]);
                }

                if (candidate.Qty <= StockQuantityRules.QtyTolerance)
                {
                    throw new OrderHuReservationApplyException(
                        "HU_NOT_AVAILABLE",
                        $"HU '{huCode}' не имеет положительного количества для резерва.",
                    [$"HU '{huCode}': qty={candidate.Qty}"]);
                }

                var lineRemainingAfterSelected = Math.Max(0, remainingQty - reservedQty);
                if (lineRemainingAfterSelected <= StockQuantityRules.QtyTolerance)
                {
                    break;
                }

                if (candidate.Qty > lineRemainingAfterSelected + StockQuantityRules.QtyTolerance)
                {
                    throw new OrderHuReservationApplyException(
                        "HU_QTY_EXCEEDS_REMAINING",
                        $"HU '{huCode}' больше остатка строки заказа.",
                        [
                            $"HU '{huCode}': qty={candidate.Qty:0.###}, remaining={lineRemainingAfterSelected:0.###}.",
                            "HU резервируется целиком; частичный резерв паллеты недоступен."
                        ]);
                }

                reservedQty += candidate.Qty;
                selectedHu.Add(new OrderHuReservationAppliedHuResult
                {
                    HuCode = candidate.HuCode,
                    Source = candidate.Source,
                    Qty = candidate.Qty,
                    ShipReady = candidate.ShipReady
                });

                replacementPlanLines.Add(new OrderReceiptPlanLine
                {
                    OrderId = customerOrderId,
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyPlanned = candidate.Qty,
                    ToHu = candidate.HuCode,
                    SortOrder = sortOrder++
                });
            }

            appliedLines.Add(BuildAppliedLineResult(orderLine, selectedHu, reservedQty));
        }

        store.ReplaceOrderReceiptPlanLinesForOrderLines(
            customerOrderId,
            affectedOrderLineIds,
            replacementPlanLines);
        if (replacementPlanLines.Any(line => line.QtyPlanned > StockQuantityRules.QtyTolerance)
            && !order.UseReservedStock)
        {
            store.UpdateOrder(CopyOrderWithReservedStock(order));
        }

        new OrderService(store).RefreshPersistedStatus(customerOrderId);

        return new OrderHuReservationApplyResult
        {
            Ok = true,
            OrderId = customerOrderId,
            AppliedLines = appliedLines
        };
    }

    private Dictionary<string, HuReservationCandidateResult> BuildAvailableCandidatesByHu(
        HuReservationCandidatesService candidatesService,
        long customerOrderId,
        OrderLine orderLine,
        double qtyOrdered)
    {
        var candidatesResult = candidatesService.Build(new HuReservationCandidatesQuery
        {
            OrderId = customerOrderId,
            Lines =
            [
                new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = orderLine.Id.ToString(),
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyOrdered = qtyOrdered
                }
            ],
            ExcludeHuCodes = Array.Empty<string>()
        });

        var lineCandidates = candidatesResult.Lines.FirstOrDefault(line => line.OrderLineId == orderLine.Id)
                             ?? candidatesResult.Lines.FirstOrDefault();
        if (lineCandidates == null)
        {
            return new Dictionary<string, HuReservationCandidateResult>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, HuReservationCandidateResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in lineCandidates.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.HuCode))
            {
                continue;
            }

            var normalizedHu = candidate.HuCode.Trim().ToUpperInvariant();
            if (!map.ContainsKey(normalizedHu))
            {
                map[normalizedHu] = candidate;
            }
        }

        return map;
    }

    private static void ValidateDuplicateHuInRequest(
        IReadOnlyList<string> selectedHuCodes,
        long orderLineId,
        IDictionary<string, long> huToRequestLine)
    {
        foreach (var huCode in selectedHuCodes)
        {
            if (huToRequestLine.TryGetValue(huCode, out var existingLineId)
                && existingLineId != orderLineId)
            {
                throw new OrderHuReservationApplyException(
                    "DUPLICATE_HU_IN_REQUEST",
                    $"HU '{huCode}' выбран более одного раза в одном запросе.",
                    [$"HU '{huCode}': lines {existingLineId} и {orderLineId}"]);
            }

            huToRequestLine[huCode] = orderLineId;
        }
    }

    private static void ValidateHuNotReservedOnOtherLinesOfSameOrder(
        long customerOrderId,
        long orderLineId,
        IReadOnlyList<string> selectedHuCodes,
        IReadOnlySet<long> affectedOrderLineIds,
        IReadOnlyList<OrderReceiptPlanLine> existingPlanLines)
    {
        if (selectedHuCodes.Count == 0)
        {
            return;
        }

        var selectedSet = new HashSet<string>(selectedHuCodes, StringComparer.OrdinalIgnoreCase);
        foreach (var planLine in existingPlanLines)
        {
            if (planLine.OrderId != customerOrderId
                || planLine.QtyPlanned <= StockQuantityRules.QtyTolerance
                || string.IsNullOrWhiteSpace(planLine.ToHu))
            {
                continue;
            }

            var normalizedHu = planLine.ToHu.Trim().ToUpperInvariant();
            if (!selectedSet.Contains(normalizedHu))
            {
                continue;
            }

            if (planLine.OrderLineId == orderLineId)
            {
                continue;
            }

            if (affectedOrderLineIds.Contains(planLine.OrderLineId))
            {
                continue;
            }

            throw new OrderHuReservationApplyException(
                "HU_RESERVED_BY_OTHER_ORDER",
                $"HU '{normalizedHu}' уже зарезервирован другой строкой этого заказа.",
                [$"HU '{normalizedHu}': order_line_id={planLine.OrderLineId}"]);
        }
    }

    private static OrderHuReservationApplyLineResult BuildAppliedLineResult(
        OrderLine orderLine,
        IReadOnlyList<OrderHuReservationAppliedHuResult> selectedHu,
        double reservedQty)
    {
        return new OrderHuReservationApplyLineResult
        {
            OrderLineId = orderLine.Id,
            ItemId = orderLine.ItemId,
            OrderedQty = orderLine.QtyOrdered,
            ReservedQty = reservedQty,
            SelectedHuCount = selectedHu.Count,
            SelectedHu = selectedHu
        };
    }

    private static IReadOnlyList<string> NormalizeHuCodes(IReadOnlyList<string>? huCodes)
    {
        if (huCodes == null || huCodes.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var huCode in huCodes)
        {
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            var normalizedHu = huCode.Trim().ToUpperInvariant();
            if (seen.Add(normalizedHu))
            {
                normalized.Add(normalizedHu);
            }
        }

        return normalized;
    }

    private static bool IsAllowedSource(string source)
    {
        return string.Equals(source, SourceLedgerStock, StringComparison.OrdinalIgnoreCase);
    }

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
