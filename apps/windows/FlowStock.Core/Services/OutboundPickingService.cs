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

    public OutboundPickingService(IDataStore store, DocumentService documents)
    {
        _store = store;
        _documents = documents;
    }

    public IReadOnlyList<OutboundPickingOrderRow> GetOrders()
    {
        return _store.GetOrders()
            .Where(IsAcceptedCustomerOrder)
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
                PickedHuCount = details.PickedHuCount
            })
            .ToArray();
    }

    public OutboundPickingOrderDetails GetDetails(long orderId)
    {
        var order = EnsureAcceptedCustomerOrder(orderId);
        var expected = BuildExpectedHus(order);
        var draft = FindDraftOutbound(orderId);
        var pickedHuCodes = draft == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : _store.GetDocLines(draft.Id)
                .Select(line => NormalizeHu(line.FromHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new OutboundPickingOrderDetails
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            PartnerName = order.PartnerName ?? string.Empty,
            Status = order.StatusDisplay,
            DraftOutboundDocId = draft?.Id,
            DraftOutboundDocRef = draft?.DocRef,
            ExpectedHuCount = expected.Count,
            PickedHuCount = expected.Count(hu => pickedHuCodes.Contains(hu.HuCode)),
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
            var order = EnsureAcceptedCustomerOrder(orderId);
            var expected = BuildExpectedHus(order);
            var expectedHu = expected.FirstOrDefault(hu => string.Equals(hu.HuCode, normalizedHu, StringComparison.OrdinalIgnoreCase));
            if (expectedHu == null)
            {
                return OutboundPickingScanResult.Failure("HU_NOT_EXPECTED", "HU не ожидается для выбранного заказа.");
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

            return new OutboundPickingScanResult
            {
                Success = true,
                Message = "HU подобрана.",
                Order = GetDetails(order.Id)
            };
        }
        catch (InvalidOperationException ex)
        {
            return OutboundPickingScanResult.Failure("VALIDATION_ERROR", ex.Message);
        }
    }

    public OutboundPickingCompleteResult Complete(long orderId)
    {
        try
        {
            var order = EnsureAcceptedCustomerOrder(orderId);
            var details = GetDetails(order.Id);
            if (details.ExpectedHuCount == 0)
            {
                return OutboundPickingCompleteResult.Failure("NO_EXPECTED_HU", "Для заказа нет ожидаемых HU к отгрузке.");
            }

            if (!details.IsComplete)
            {
                return OutboundPickingCompleteResult.Failure("PICKING_INCOMPLETE", "Не все паллеты подобраны.");
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
                Message = "Все паллеты подобраны. Ожидает проведения в WPF.",
                Order = GetDetails(order.Id)
            };
        }
        catch (InvalidOperationException ex)
        {
            return OutboundPickingCompleteResult.Failure("VALIDATION_ERROR", ex.Message);
        }
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
        var orderLines = _store.GetOrderLines(order.Id)
            .GroupBy(line => line.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var orderLineIdsByItem = orderLines.Values
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Select(line => line.Id).ToArray());

        var itemNames = _store.GetItems(null).ToDictionary(item => item.Id, item => item.Name);
        var locations = _store.GetLocations().ToDictionary(location => location.Id, location => location.Code);
        var contextByKey = _store.GetHuOrderContextRows()
            .Where(context => context.ReservedCustomerOrderId == order.Id)
            .GroupBy(context => BuildHuItemKey(context.HuCode, context.ItemId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return _store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Where(row => contextByKey.ContainsKey(BuildHuItemKey(row.HuCode, row.ItemId)))
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var huCode = group.Key ?? string.Empty;
                var pallet = string.IsNullOrWhiteSpace(huCode) ? null : _store.GetProductionPalletByHu(huCode);
                var lines = group
                    .OrderBy(row => row.ItemId)
                    .Select(row =>
                    {
                        var orderLineId = ResolveOrderLineId(row.ItemId, pallet, orderLines, orderLineIdsByItem);
                        return new OutboundPickingHuLine
                        {
                            ItemId = row.ItemId,
                            ItemName = itemNames.TryGetValue(row.ItemId, out var itemName) ? itemName : string.Empty,
                            OrderLineId = orderLineId,
                            LocationId = row.LocationId,
                            LocationCode = locations.TryGetValue(row.LocationId, out var locationCode) ? locationCode : string.Empty,
                            Qty = row.Qty
                        };
                    })
                    .ToArray();

                return new ExpectedHu(
                    huCode,
                    lines.Sum(line => line.Qty),
                    string.Join(", ", lines.Select(line => string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : line.ItemName).Distinct()),
                    lines);
            })
            .Where(hu => !string.IsNullOrWhiteSpace(hu.HuCode) && hu.Lines.Count > 0)
            .ToArray();
    }

    private static long? ResolveOrderLineId(
        long itemId,
        ProductionPallet? pallet,
        IReadOnlyDictionary<long, OrderLine> orderLines,
        IReadOnlyDictionary<long, long[]> orderLineIdsByItem)
    {
        var palletLine = pallet?.Lines.FirstOrDefault(line => line.ItemId == itemId && line.OrderLineId.HasValue);
        if (palletLine?.OrderLineId.HasValue == true && orderLines.ContainsKey(palletLine.OrderLineId.Value))
        {
            return palletLine.OrderLineId.Value;
        }

        if (pallet?.OrderLineId.HasValue == true
            && orderLines.TryGetValue(pallet.OrderLineId.Value, out var orderLine)
            && orderLine.ItemId == itemId)
        {
            return pallet.OrderLineId.Value;
        }

        return orderLineIdsByItem.TryGetValue(itemId, out var ids) && ids.Length == 1 ? ids[0] : null;
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

    private Order EnsureAcceptedCustomerOrder(long orderId)
    {
        var order = _store.GetOrder(orderId);
        if (order == null)
        {
            throw new InvalidOperationException("Заказ не найден.");
        }

        if (!IsAcceptedCustomerOrder(order))
        {
            throw new InvalidOperationException("Для подбора доступны только клиентские заказы в статусе Готов.");
        }

        return order;
    }

    private static bool IsAcceptedCustomerOrder(Order order)
    {
        return order.Type == OrderType.Customer && order.Status == OrderStatus.Accepted;
    }

    private static string BuildHuItemKey(string? huCode, long itemId)
    {
        return $"{NormalizeHu(huCode)}|{itemId}";
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private sealed record ExpectedHu(
        string HuCode,
        double Qty,
        string ItemSummary,
        IReadOnlyList<OutboundPickingHuLine> Lines);
}
