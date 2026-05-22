using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderHuReservationApplyService
{
    public const string SourceLedgerStock = "LEDGER_STOCK";

    private readonly IDataStore _dataStore;
    private readonly HuReservationCandidatesService _candidatesService;

    public OrderHuReservationApplyService(IDataStore dataStore)
    {
        _dataStore = dataStore;
        _candidatesService = new HuReservationCandidatesService(dataStore);
    }

    public OrderHuReservationApplyResult Apply(long customerOrderId, OrderHuReservationApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        if (_dataStore is not IOptimizedHuReservationCandidatesStore)
        {
            throw new InvalidOperationException("Хранилище не поддерживает read-model кандидатов HU.");
        }

        var order = _dataStore.GetOrder(customerOrderId);
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

        var orderLines = _dataStore.GetOrderLines(customerOrderId)
            .Where(line => line.Id > 0)
            .ToDictionary(line => line.Id);
        if (orderLines.Count == 0)
        {
            throw new OrderHuReservationApplyException(
                "INVALID_REQUEST",
                "У заказа нет строк для применения HU.");
        }

        var shipmentRemainingByLine = _dataStore.GetOrderShipmentRemaining(customerOrderId)
            .ToDictionary(line => line.OrderLineId);
        var existingPlanLines = _dataStore.GetOrderReceiptPlanLines(customerOrderId);
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
                customerOrderId,
                orderLine,
                shipmentLine.QtyOrdered);
            var selectedHu = new List<OrderHuReservationAppliedHuResult>();
            var reservedQty = 0d;
            var sortOrder = 0;

            foreach (var huCode in selectedHuCodes)
            {
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

            if (reservedQty > remainingQty + StockQuantityRules.QtyTolerance)
            {
                throw new OrderHuReservationApplyException(
                    "SELECTED_QTY_EXCEEDS_LINE_REMAINING",
                    $"Сумма выбранных HU ({reservedQty}) превышает неотгруженный остаток строки ({remainingQty}).",
                    [
                        $"order_line_id={orderLine.Id}: selected={reservedQty}, remaining={remainingQty}, ordered={shipmentLine.QtyOrdered}, shipped={shipmentLine.QtyShipped}"
                    ]);
            }

            appliedLines.Add(BuildAppliedLineResult(orderLine, selectedHu, reservedQty));
        }

        _dataStore.ReplaceOrderReceiptPlanLinesForOrderLines(
            customerOrderId,
            affectedOrderLineIds,
            replacementPlanLines);

        return new OrderHuReservationApplyResult
        {
            Ok = true,
            OrderId = customerOrderId,
            AppliedLines = appliedLines
        };
    }

    private Dictionary<string, HuReservationCandidateResult> BuildAvailableCandidatesByHu(
        long customerOrderId,
        OrderLine orderLine,
        double qtyOrdered)
    {
        var candidatesResult = _candidatesService.Build(new HuReservationCandidatesQuery
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
}
