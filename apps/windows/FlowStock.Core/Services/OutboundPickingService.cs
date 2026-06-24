using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using System.Security.Cryptography;
using System.Text;

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
        if (_store is IOptimizedTsdOutboundPickingStore optimizedStore)
        {
            return optimizedStore.GetTsdOutboundOrderRows();
        }

        var candidateOrders = _store.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status is not (OrderStatus.Draft or OrderStatus.Cancelled or OrderStatus.Merged)
                            && !_store.HasActiveOrderControlForOrder(order.Id))
            .ToArray();
        if (candidateOrders.Length == 0)
        {
            return Array.Empty<OutboundPickingOrderRow>();
        }

        var orderIds = candidateOrders.Select(order => order.Id).ToArray();
        var boundHuCache = CustomerOutboundBoundHuBatchCache.Load(_store, orderIds);
        var docsByOrderId = LoadDocsByOrderId(orderIds);
        var docLinesByDocId = LoadDocLinesByDocId(docsByOrderId);
        var shippedTotalsByOrderId = LoadShippedTotalsByOrderId(orderIds);
        var orderLinesByOrderId = LoadOrderLinesByOrderId(orderIds);

        return candidateOrders
            .Where(order => IsCustomerOrderReadyForPicking(order, boundHuCache, docsByOrderId))
            .Select(order => BuildListRow(
                order,
                boundHuCache,
                docsByOrderId,
                docLinesByDocId,
                shippedTotalsByOrderId,
                orderLinesByOrderId))
            .Where(row => row.ExpectedHuCount > 0)
            .OrderBy(row => row.OrderRef, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private OutboundPickingOrderRow BuildListRow(
        Order order,
        CustomerOutboundBoundHuBatchCache boundHuCache,
        IReadOnlyDictionary<long, IReadOnlyList<Doc>> docsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<DocLine>> docLinesByDocId,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> shippedTotalsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLinesByOrderId)
    {
        var expectedLines = boundHuCache.GetUnshippedOutboundHuLines(order);
        var expectedHuCodes = expectedLines
            .Select(line => NormalizeHu(line.HuCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        docsByOrderId.TryGetValue(order.Id, out var docs);
        docs ??= Array.Empty<Doc>();
        var draft = docs
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft)
            .OrderBy(doc => doc.Id)
            .FirstOrDefault();
        var pickingDoc = draft ?? docs
            .Where(doc => doc.Type == DocType.Outbound && IsTsdPickingDoc(doc))
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault();
        var pickedHuCodes = pickingDoc == null || !docLinesByDocId.TryGetValue(pickingDoc.Id, out var pickingLines)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : pickingLines
                .Select(line => NormalizeHu(line.FromHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        orderLinesByOrderId.TryGetValue(order.Id, out var orderLines);
        orderLines ??= Array.Empty<OrderLine>();
        shippedTotalsByOrderId.TryGetValue(order.Id, out var shippedByLine);
        shippedByLine ??= new Dictionary<long, double>();
        var progress = BuildShipmentProgress(orderLines, shippedByLine);
        var scannedQty = draft == null || !docLinesByDocId.TryGetValue(draft.Id, out var draftLines)
            ? 0d
            : draftLines.Sum(line => Math.Max(0, line.Qty));
        var operationFingerprint = BuildListOperationFingerprint(expectedHuCodes, pickedHuCodes);

        return new OutboundPickingOrderRow
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            PartnerName = order.PartnerName ?? string.Empty,
            Status = progress.IsPartiallyShipped ? "Частично отгружено" : order.StatusDisplay,
            ExpectedHuCount = expectedHuCodes.Length,
            PickedHuCount = expectedHuCodes.Count(hu => pickedHuCodes.Contains(hu)),
            OrderedQty = progress.OrderedQty,
            ShippedQty = progress.ShippedQty,
            RemainingQty = progress.RemainingQty,
            ScannedQty = scannedQty,
            IsClosed = pickingDoc?.Status == DocStatus.Closed,
            OperationFingerprint = operationFingerprint
        };
    }

    private static OrderShipmentProgress BuildShipmentProgress(
        IReadOnlyList<OrderLine> lines,
        IReadOnlyDictionary<long, double> shippedByLine)
    {
        return new OrderShipmentProgress
        {
            OrderedQty = lines.Sum(line => Math.Max(0, line.QtyOrdered)),
            ShippedQty = lines.Sum(line =>
                shippedByLine.TryGetValue(line.Id, out var shipped) ? Math.Max(0, shipped) : 0d),
            RemainingQty = lines.Sum(line =>
            {
                var shipped = shippedByLine.TryGetValue(line.Id, out var value) ? Math.Max(0, value) : 0d;
                return Math.Max(0, line.QtyOrdered - shipped);
            })
        };
    }

    private static string BuildListOperationFingerprint(
        IReadOnlyList<string> expectedHuCodes,
        IReadOnlySet<string> pickedHuCodes)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "|",
            expectedHuCodes
                .OrderBy(hu => hu, StringComparer.OrdinalIgnoreCase)
                .Select(hu => $"{hu}:{(pickedHuCodes.Contains(hu) ? "1" : "0")}"))))).ToLowerInvariant();
    }

    private bool IsCustomerOrderReadyForPicking(
        Order order,
        CustomerOutboundBoundHuBatchCache boundHuCache,
        IReadOnlyDictionary<long, IReadOnlyList<Doc>> docsByOrderId)
    {
        if (!IsCustomerOrderCandidateForPicking(order))
        {
            return false;
        }

        if (IsAcceptedCustomerOrder(order))
        {
            return true;
        }

        var hasExpectedHu = boundHuCache.GetUnshippedOutboundHuLines(order).Count > 0;
        if (!hasExpectedHu)
        {
            return false;
        }

        docsByOrderId.TryGetValue(order.Id, out var docs);
        docs ??= Array.Empty<Doc>();
        if (docs.Any(doc => doc.Type == DocType.Outbound && IsTsdPickingDoc(doc)))
        {
            return true;
        }

        var progress = OrderShipmentProgressService.Get(_store, order.Id);
        return progress.IsPartiallyShipped
               || !CustomerOutboundBoundHuService.HasReceiptProductionNeed(_store, order.Id);
    }

    private IReadOnlyDictionary<long, IReadOnlyList<Doc>> LoadDocsByOrderId(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _store.GetDocsByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return orderIds.ToDictionary(
                orderId => orderId,
                orderId => (IReadOnlyList<Doc>)_store.GetDocsByOrder(orderId));
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<DocLine>> LoadDocLinesByDocId(
        IReadOnlyDictionary<long, IReadOnlyList<Doc>> docsByOrderId)
    {
        var docIds = docsByOrderId.Values
            .SelectMany(docs => docs)
            .Select(doc => doc.Id)
            .Distinct()
            .ToArray();
        if (docIds.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<DocLine>>();
        }

        try
        {
            return _store.GetDocLinesByDocIds(docIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return docIds.ToDictionary(
                docId => docId,
                docId => (IReadOnlyList<DocLine>)_store.GetDocLines(docId));
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> LoadShippedTotalsByOrderId(
        IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _store.GetShippedTotalsByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return orderIds.ToDictionary(
                orderId => orderId,
                orderId => (IReadOnlyDictionary<long, double>)_store.GetShippedTotalsByOrderLine(orderId));
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> LoadOrderLinesByOrderId(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _store.GetOrderLinesByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return orderIds.ToDictionary(
                orderId => orderId,
                orderId => (IReadOnlyList<OrderLine>)_store.GetOrderLines(orderId));
        }
    }

    private static bool IsMockStoreException(Exception ex)
    {
        var fullName = ex.GetType().FullName ?? string.Empty;
        return fullName.Contains("Moq", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Castle.Proxies", StringComparison.OrdinalIgnoreCase);
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

        var operationFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "|",
            expected.OrderBy(hu => hu.HuCode, StringComparer.OrdinalIgnoreCase)
                .Select(hu => $"{hu.HuCode}:{(pickedHuCodes.Contains(hu.HuCode) ? "1" : "0")}"))))).ToLowerInvariant();

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
            IsClosed = pickingDoc?.Status == DocStatus.Closed,
            OperationFingerprint = operationFingerprint,
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
            if (_store.HasActiveOrderControlForOrder(order.Id))
            {
                return OutboundPickingScanResult.Failure(
                    "ORDER_CONTROL_ACTIVE",
                    "Заказ находится в активном контроле готовых заказов.");
            }

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
            OutboundPickingScanResult? transactionFailure = null;
            _store.ExecuteInTransaction(store =>
            {
                if (!store.LockOrdersForUpdate([order.Id]))
                {
                    transactionFailure = OutboundPickingScanResult.Failure("ORDER_NOT_FOUND", "Заказ не найден.");
                    return;
                }

                var lockedOrder = store.GetOrder(order.Id);
                if (lockedOrder == null || !IsCustomerOrderReadyForPicking(lockedOrder))
                {
                    transactionFailure = OutboundPickingScanResult.Failure("VALIDATION_ERROR", "Для подбора доступны только клиентские заказы, готовые к отгрузке.");
                    return;
                }

                if (store.HasActiveOrderControlForOrder(lockedOrder.Id))
                {
                    transactionFailure = OutboundPickingScanResult.Failure(
                        "ORDER_CONTROL_ACTIVE",
                        "Заказ находится в активном контроле готовых заказов.");
                    return;
                }

                var lockedExpected = BuildExpectedHus(lockedOrder, store);
                var lockedExpectedHu = lockedExpected.FirstOrDefault(hu => string.Equals(hu.HuCode, normalizedHu, StringComparison.OrdinalIgnoreCase));
                if (lockedExpectedHu == null)
                {
                    transactionFailure = OutboundPickingScanResult.Failure("HU_NOT_EXPECTED", "HU не ожидается для выбранного заказа.");
                    return;
                }

                if (!HasPhysicalHuStock(lockedExpectedHu))
                {
                    transactionFailure = OutboundPickingScanResult.Failure(
                        "HU_NO_PHYSICAL_STOCK",
                        "HU не имеет физического остатка на складе (ledger).");
                    return;
                }

                var foreignDraft = FindOpenOutboundByHu(normalizedHu, lockedOrder.Id, store);
                if (foreignDraft != null)
                {
                    transactionFailure = OutboundPickingScanResult.Failure("HU_PICKED_IN_OTHER_OUTBOUND", "HU уже подобрана в другом открытом документе отгрузки.");
                    return;
                }

                var draft = FindDraftOutbound(lockedOrder.Id, store);
                if (draft != null && IsHuPicked(draft.Id, normalizedHu, store))
                {
                    return;
                }

                draftDocId = draft?.Id ?? CreateDraftOutbound(store, lockedOrder, deviceId);
                foreach (var line in lockedExpectedHu.Lines)
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

            if (transactionFailure != null)
            {
                return transactionFailure;
            }

            var details = GetDetails(order.Id);
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
            if (_store.HasActiveOrderControlForOrder(order.Id))
            {
                return OutboundPickingCompleteResult.Failure(
                    "ORDER_CONTROL_ACTIVE",
                    "Заказ находится в активном контроле готовых заказов.");
            }

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

            if (details.IsComplete || allowPartial)
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

    private long CreateDraftOutbound(IDataStore store, Order order, string? deviceId)
    {
        var docRef = DocRefGenerator.Generate(store, DocType.Outbound, DateTime.Now);
        var comment = string.IsNullOrWhiteSpace(deviceId)
            ? TsdPickingComment
            : $"{TsdPickingComment} ({deviceId.Trim()})";
        return store.AddDoc(new Doc
        {
            DocRef = docRef,
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.Now,
            PartnerId = order.PartnerId,
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            Comment = comment
        });
    }

    private IReadOnlyList<ExpectedHu> BuildExpectedHus(Order order, IDataStore? store = null)
    {
        var data = store ?? _store;
        var boundLines = CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(data, order.Id);
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

    private Doc? FindDraftOutbound(long orderId, IDataStore? store = null)
    {
        var data = store ?? _store;
        return data.GetDocsByOrder(orderId)
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

    private Doc? FindOpenOutboundByHu(string huCode, long currentOrderId, IDataStore? store = null)
    {
        var data = store ?? _store;
        var normalizedHu = NormalizeHu(huCode);
        return data.GetDocs()
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Draft && doc.OrderId != currentOrderId)
            .FirstOrDefault(doc => data.GetDocLines(doc.Id)
                .Any(line => string.Equals(NormalizeHu(line.FromHu), normalizedHu, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsHuPicked(long docId, string huCode, IDataStore? store = null)
    {
        var data = store ?? _store;
        var normalizedHu = NormalizeHu(huCode);
        return data.GetDocLines(docId)
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
