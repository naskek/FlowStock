using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OutboundPickingService
{
    private const double QtyTolerance = 0.000001d;
    private const string TsdPickingComment = "TSD OUTBOUND PICKING";
    private const string TsdPickingReadyComment = "TSD OUTBOUND PICKING READY";
    private readonly IDataStore _store;
    private readonly DocumentService _documents;
    private readonly FlowStockLedgerFlowOptions _options;

    public OutboundPickingService(IDataStore store, DocumentService documents)
        : this(store, documents, new FlowStockLedgerFlowOptions())
    {
    }

    public OutboundPickingService(
        IDataStore store,
        DocumentService documents,
        FlowStockLedgerFlowOptions options)
    {
        _store = store;
        _documents = documents;
        _options = options;
    }

    public IReadOnlyList<OutboundPickingOrderRow> GetOrders()
    {
        return _store.GetOrders()
            .Where(IsCustomerOrderReadyForPicking)
            .Select(order => GetDetails(order.Id))
            .Where(details => details.ExpectedHuCount > 0)
            .OrderBy(details => details.OrderRef, StringComparer.CurrentCultureIgnoreCase)
            .Select(details => new OutboundPickingOrderRow
            {
                OrderId = details.OrderId,
                OrderRef = details.OrderRef,
                PartnerName = details.PartnerName,
                Status = details.Status,
                ExpectedHuCount = details.ExpectedHuCount,
                PickedHuCount = details.PickedHuCount,
                OrderedQty = details.OrderedQty,
                ShippedQty = details.ShippedQty,
                RemainingQty = details.RemainingQty,
                ScannedQty = details.ScannedQty
            })
            .ToArray();
    }

    public OutboundPickingOrderDetails GetDetails(long orderId)
    {
        var order = EnsureCustomerOrderForPickingView(orderId);
        var expected = BuildExpectedHus(order);
        var draft = FindDraftOutbound(orderId);
        var pickingDoc = draft ?? FindTsdPickingOutbound(orderId);
        var pickedHuCodes = pickingDoc == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : _store.GetDocLines(pickingDoc.Id)
                .Select(line => NormalizeHu(line.FromHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var progress = OrderShipmentProgressService.Get(_store, order.Id);
        var scannedQty = draft == null
            ? 0d
            : _store.GetDocLines(draft.Id).Sum(line => Math.Max(0, line.Qty));

        return new OutboundPickingOrderDetails
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            PartnerName = order.PartnerName ?? string.Empty,
            Status = progress.IsPartiallyShipped ? "Частично отгружено" : order.StatusDisplay,
            DraftOutboundDocId = draft?.Id ?? pickingDoc?.Id,
            DraftOutboundDocRef = draft?.DocRef ?? pickingDoc?.DocRef,
            ExpectedHuCount = expected.Count,
            PickedHuCount = expected.Count(hu => pickedHuCodes.Contains(hu.HuCode)),
            OrderedQty = progress.OrderedQty,
            ShippedQty = progress.ShippedQty,
            RemainingQty = progress.RemainingQty,
            ScannedQty = scannedQty,
            Hus = expected
                .OrderBy(hu => hu.HuCode, StringComparer.OrdinalIgnoreCase)
                .Select(hu => new OutboundPickingHuRow
                {
                    HuCode = hu.HuCode,
                    Status = pickedHuCodes.Contains(hu.HuCode) ? OutboundPickingHuStatus.Picked : OutboundPickingHuStatus.Pending,
                    Qty = hu.Qty,
                    ItemSummary = hu.ItemSummary,
                    Lines = hu.Lines
                })
                .ToArray()
        };
    }

    public OutboundPickingScanResult Scan(long orderId, string huCode, string? deviceId)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return OutboundPickingScanResult.Failure("HU_REQUIRED", "Отсканируйте HU.");
        }

        try
        {
            var alreadyPickedClosed = TryBuildAlreadyPickedClosedScan(orderId, normalizedHu);
            if (alreadyPickedClosed != null)
            {
                return alreadyPickedClosed;
            }

            var order = EnsureCustomerOrderReadyForPicking(orderId);
            var expected = BuildExpectedHus(order);
            var expectedHu = expected.FirstOrDefault(hu => string.Equals(hu.HuCode, normalizedHu, StringComparison.OrdinalIgnoreCase));
            if (expectedHu == null)
            {
                if (IsBoundHuWithoutPhysicalStock(order.Id, normalizedHu))
                {
                    return OutboundPickingScanResult.Failure(
                        "HU_BOUND_WITHOUT_STOCK",
                        "HU привязан к заказу, но физически не принят или отсутствует на складе.");
                }

                return OutboundPickingScanResult.Failure("HU_NOT_EXPECTED", "HU не ожидается для выбранного заказа.");
            }

            if (!HasPhysicalHuStock(expectedHu))
            {
                return OutboundPickingScanResult.Failure(
                    "HU_NO_PHYSICAL_STOCK",
                    "HU не имеет физического остатка на складе (ledger).");
            }

            var foreignDraft = FindOpenOutboundByHu(normalizedHu, order.Id);
            if (foreignDraft != null)
            {
                return OutboundPickingScanResult.Failure("HU_PICKED_IN_OTHER_OUTBOUND", "HU уже подобрана в другом открытом документе отгрузки.");
            }

            var existingDraft = FindDraftOutbound(order.Id);
            if (existingDraft != null && IsHuPicked(existingDraft.Id, normalizedHu))
            {
                return new OutboundPickingScanResult
                {
                    Success = true,
                    AlreadyPicked = true,
                    Message = "HU уже подобрана.",
                    Order = GetDetails(order.Id)
                };
            }

            var remainingByOrderLine = _store.GetOrderShipmentRemaining(order.Id)
                .Where(line => line.QtyRemaining > QtyTolerance)
                .ToDictionary(line => line.OrderLineId, line => line.QtyRemaining);
            var pickedByOrderLine = BuildOpenPickedQtyByOrderLine(order.Id);
            foreach (var line in expectedHu.Lines)
            {
                if (!line.OrderLineId.HasValue)
                {
                    return OutboundPickingScanResult.Failure("ORDER_LINE_NOT_FOUND", "Не найдена строка заказа для HU.");
                }

                if (!remainingByOrderLine.TryGetValue(line.OrderLineId.Value, out var remaining))
                {
                    return OutboundPickingScanResult.Failure("NO_SHIPMENT_REMAINING", "По строке заказа не осталось количества к отгрузке.");
                }

                pickedByOrderLine.TryGetValue(line.OrderLineId.Value, out var alreadyPicked);
                if (alreadyPicked + line.Qty > remaining + QtyTolerance)
                {
                    return OutboundPickingScanResult.Failure("SHIPMENT_REMAINING_EXCEEDED", "Подбор превышает остаток к отгрузке.");
                }
            }

            var draftDocId = 0L;
            _store.ExecuteInTransaction(store =>
            {
                var draft = FindDraftOutbound(order.Id);
                draftDocId = draft?.Id ?? CreateDraftOutbound(order, deviceId);
                foreach (var line in expectedHu.Lines)
                {
                    store.AddDocLine(new DocLine
                    {
                        DocId = draftDocId,
                        OrderLineId = line.OrderLineId,
                        ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                        ItemId = line.ItemId,
                        Qty = line.Qty,
                        FromLocationId = line.LocationId,
                        ToLocationId = null,
                        FromHu = normalizedHu,
                        ToHu = null
                    });
                }
            });

            var details = GetDetails(order.Id);
            if (_options.OutboundAutoCloseOnComplete && details.IsComplete)
            {
                var autoClose = TryAutoCloseOutbound(order.Id);
                if (!autoClose.Success)
                {
                    return OutboundPickingScanResult.Failure(autoClose.ErrorCode ?? "OUTBOUND_CLOSE_FAILED", autoClose.Message);
                }

                return new OutboundPickingScanResult
                {
                    Success = true,
                    Message = autoClose.Message,
                    OutboundClosed = true,
                    ClosedOutboundDocRef = autoClose.ClosedDocRef,
                    Order = autoClose.Order ?? details
                };
            }

            return new OutboundPickingScanResult
            {
                Success = true,
                Message = "HU подобрана.",
                Order = details
            };
        }
        catch (InvalidOperationException ex)
        {
            return OutboundPickingScanResult.Failure("VALIDATION_ERROR", ex.Message);
        }
    }

    private OutboundPickingScanResult? TryBuildAlreadyPickedClosedScan(long orderId, string huCode)
    {
        var pickingDoc = _store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Closed)
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault(doc => IsHuPicked(doc.Id, huCode));
        if (pickingDoc == null)
        {
            return null;
        }

        return OutboundPickingScanResult.Failure(
            "HU_ALREADY_SHIPPED",
            $"HU уже отгружен по этому заказу ({pickingDoc.DocRef}).");
    }

    public OutboundPickingCompleteResult Complete(long orderId, bool allowPartial = false)
    {
        try
        {
            var order = _store.GetOrder(orderId)
                ?? throw new InvalidOperationException("Заказ не найден.");
            if (order.Type != OrderType.Customer)
            {
                throw new InvalidOperationException("Для подбора доступны только клиентские заказы.");
            }

            var closedPickingDoc = FindTsdPickingOutbound(orderId);
            if (order.Status == OrderStatus.Shipped
                && closedPickingDoc?.Status == DocStatus.Closed)
            {
                return new OutboundPickingCompleteResult
                {
                    Success = true,
                    OutboundClosed = true,
                    ClosedOutboundDocId = closedPickingDoc.Id,
                    ClosedOutboundDocRef = closedPickingDoc.DocRef,
                    Message = $"Отгрузка уже проведена ({closedPickingDoc.DocRef}).",
                    Order = GetDetails(orderId)
                };
            }

            order = EnsureCustomerOrderReadyForPicking(orderId);
            var details = GetDetails(order.Id);
            if (details.ExpectedHuCount == 0)
            {
                return OutboundPickingCompleteResult.Failure("NO_EXPECTED_HU", "Для заказа нет ожидаемых HU к отгрузке.");
            }

            if (details.PickedHuCount == 0)
            {
                return OutboundPickingCompleteResult.Failure("NOTHING_PICKED", "Не отсканировано ни одной HU.");
            }

            if (!details.IsComplete && !allowPartial)
            {
                return OutboundPickingCompleteResult.Failure(
                    "PARTIAL_CONFIRMATION_REQUIRED",
                    $"Отгружено {FormatQty(details.ScannedQty)} из {FormatQty(details.RemainingQty)}. Подтвердите частичную отгрузку.");
            }

            if (_options.OutboundAutoCloseOnComplete)
            {
                var autoClose = TryAutoCloseOutbound(order.Id);
                if (!autoClose.Success)
                {
                    return OutboundPickingCompleteResult.Failure(
                        autoClose.ErrorCode ?? "OUTBOUND_CLOSE_FAILED",
                        autoClose.Message);
                }

                return new OutboundPickingCompleteResult
                {
                    Success = true,
                    OutboundClosed = true,
                    ClosedOutboundDocId = autoClose.ClosedDocId,
                    ClosedOutboundDocRef = autoClose.ClosedDocRef,
                    Message = autoClose.Message,
                    Order = autoClose.Order ?? GetDetails(order.Id)
                };
            }

            var draft = FindDraftOutbound(order.Id);
            if (draft == null)
            {
                return OutboundPickingCompleteResult.Failure("DRAFT_OUTBOUND_NOT_FOUND", "Черновик отгрузки не найден.");
            }

            _store.UpdateDocComment(draft.Id, TsdPickingReadyComment);
            return new OutboundPickingCompleteResult
            {
                Success = true,
                Message = details.IsComplete
                    ? "Все паллеты подобраны. Ожидает проведения в WPF."
                    : "Частичная отгрузка подготовлена. Ожидает проведения в WPF.",
                Order = GetDetails(order.Id)
            };
        }
        catch (InvalidOperationException ex)
        {
            return OutboundPickingCompleteResult.Failure("VALIDATION_ERROR", ex.Message);
        }
    }

    private OutboundAutoCloseAttempt TryAutoCloseOutbound(long orderId)
    {
        var outbound = FindTsdPickingOutbound(orderId);
        if (outbound == null)
        {
            return OutboundAutoCloseAttempt.Failure("DRAFT_OUTBOUND_NOT_FOUND", "Черновик отгрузки не найден.");
        }

        if (outbound.Status == DocStatus.Closed)
        {
            return OutboundAutoCloseAttempt.AlreadyClosed(outbound, GetDetails(orderId));
        }

        var close = _documents.TryCloseDoc(outbound.Id, allowNegative: false);
        if (!close.Success)
        {
            var message = close.Errors.Count > 0
                ? string.Join("; ", close.Errors)
                : "Не удалось провести отгрузку.";
            return OutboundAutoCloseAttempt.Failure("OUTBOUND_CLOSE_FAILED", message);
        }

        var closedDoc = _store.GetDoc(outbound.Id);
        return OutboundAutoCloseAttempt.Closed(
            closedDoc?.DocRef ?? outbound.DocRef,
            outbound.Id,
            GetDetails(orderId));
    }

    private static bool HasPhysicalHuStock(ExpectedHu expectedHu)
    {
        return expectedHu.Lines.Sum(line => line.Qty) > QtyTolerance;
    }

    private long CreateDraftOutbound(Order order, string? deviceId)
    {
        var docRef = _documents.GenerateDocRef(DocType.Outbound, DateTime.Now);
        var comment = string.IsNullOrWhiteSpace(deviceId)
            ? TsdPickingComment
            : $"{TsdPickingComment} ({deviceId.Trim()})";
        return _documents.CreateDoc(
            DocType.Outbound,
            docRef,
            comment,
            order.PartnerId,
            order.OrderRef,
            null,
            order.Id,
            hydrateOrderLines: false);
    }

    private IReadOnlyList<ExpectedHu> BuildExpectedHus(Order order)
    {
        var boundLines = CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(_store, order.Id);
        return boundLines
            .GroupBy(line => line.HuCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var lines = group
                    .OrderBy(line => line.OrderLineId)
                    .Select(line => new OutboundPickingHuLine
                    {
                        ItemId = line.ItemId,
                        ItemName = line.ItemName,
                        OrderLineId = line.OrderLineId,
                        LocationId = line.FromLocationId ?? 0,
                        LocationCode = line.FromLocationCode ?? string.Empty,
                        Qty = line.Qty
                    })
                    .ToArray();

                return new ExpectedHu(
                    group.Key,
                    lines.Sum(line => line.Qty),
                    string.Join(
                        ", ",
                        lines.Select(line => string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : line.ItemName)
                            .Distinct()),
                    lines);
            })
            .Where(hu => hu.Lines.Count > 0)
            .ToArray();
    }

    private bool IsBoundHuWithoutPhysicalStock(long orderId, string huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return false;
        }

        var planLines = _store.GetOrderReceiptPlanLines(orderId)
            .Where(line => line.QtyPlanned > QtyTolerance
                           && string.Equals(NormalizeHu(line.ToHu), normalizedHu, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (planLines.Length == 0)
        {
            return false;
        }

        var stockByItem = _store.GetHuStockRows()
            .Where(row => string.Equals(NormalizeHu(row.HuCode), normalizedHu, StringComparison.OrdinalIgnoreCase)
                          && row.Qty > QtyTolerance)
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));

        return planLines.Any(line => !stockByItem.TryGetValue(line.ItemId, out var stockQty)
                                     || stockQty <= QtyTolerance);
    }

    private Dictionary<long, double> BuildOpenPickedQtyByOrderLine(long orderId)
    {
        return _store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft)
            .SelectMany(doc => _store.GetDocLines(doc.Id))
            .Where(line => line.OrderLineId.HasValue)
            .GroupBy(line => line.OrderLineId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));
    }

    private Doc? FindDraftOutbound(long orderId)
    {
        return _store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft)
            .OrderBy(doc => doc.Id)
            .FirstOrDefault();
    }

    private Doc? FindTsdPickingOutbound(long orderId)
    {
        return _store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.Outbound && IsTsdPickingDoc(doc))
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault();
    }

    private static bool IsTsdPickingDoc(Doc doc)
    {
        return !string.IsNullOrWhiteSpace(doc.Comment)
               && doc.Comment.StartsWith(TsdPickingComment, StringComparison.OrdinalIgnoreCase);
    }

    private Doc? FindOpenOutboundByHu(string huCode, long currentOrderId)
    {
        var normalizedHu = NormalizeHu(huCode);
        return _store.GetDocs()
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft && doc.OrderId != currentOrderId)
            .FirstOrDefault(doc => _store.GetDocLines(doc.Id)
                .Any(line => string.Equals(NormalizeHu(line.FromHu), normalizedHu, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsHuPicked(long docId, string huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        return _store.GetDocLines(docId)
            .Any(line => string.Equals(NormalizeHu(line.FromHu), normalizedHu, StringComparison.OrdinalIgnoreCase));
    }

    private Order EnsureCustomerOrderReadyForPicking(long orderId)
    {
        var order = _store.GetOrder(orderId);
        if (order == null)
        {
            throw new InvalidOperationException("Заказ не найден.");
        }

        if (!IsCustomerOrderReadyForPicking(order))
        {
            throw new InvalidOperationException("Для подбора доступны только клиентские заказы, готовые к отгрузке.");
        }

        return order;
    }

    private Order EnsureCustomerOrderForPickingView(long orderId)
    {
        var order = _store.GetOrder(orderId);
        if (order == null)
        {
            throw new InvalidOperationException("Заказ не найден.");
        }

        if (order.Type != OrderType.Customer)
        {
            throw new InvalidOperationException("Для подбора доступны только клиентские заказы.");
        }

        if (IsCustomerOrderReadyForPicking(order) || FindTsdPickingOutbound(orderId) != null)
        {
            return order;
        }

        throw new InvalidOperationException("Для подбора доступны только клиентские заказы, готовые к отгрузке.");
    }

    private static bool IsAcceptedCustomerOrder(Order order)
    {
        return order.Type == OrderType.Customer && order.Status == OrderStatus.Accepted;
    }

    private static bool IsCustomerOrderCandidateForPicking(Order order)
    {
        return order.Type == OrderType.Customer
               && order.Status is not (OrderStatus.Draft or OrderStatus.Cancelled or OrderStatus.Merged or OrderStatus.Shipped);
    }

    private bool IsCustomerOrderReadyForPicking(Order order)
    {
        if (!IsCustomerOrderCandidateForPicking(order))
        {
            return false;
        }

        if (IsAcceptedCustomerOrder(order))
        {
            return true;
        }

        var hasExpectedHu = CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(_store, order.Id).Count > 0;
        if (!hasExpectedHu)
        {
            return false;
        }

        return OrderShipmentProgressService.Get(_store, order.Id).IsPartiallyShipped
               || !CustomerOutboundBoundHuService.HasReceiptProductionNeed(_store, order.Id);
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
    }

    private sealed record ExpectedHu(
        string HuCode,
        double Qty,
        string ItemSummary,
        IReadOnlyList<OutboundPickingHuLine> Lines);
}
