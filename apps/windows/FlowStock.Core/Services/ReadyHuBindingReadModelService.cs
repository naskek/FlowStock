using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ReadyHuBindingReadModelService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;

    private readonly IDataStore _dataStore;

    public ReadyHuBindingReadModelService(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public ReadyHuBindingReadModel Build()
    {
        if (_dataStore is not IOptimizedHuReservationCandidatesStore)
        {
            return new ReadyHuBindingReadModel();
        }

        var compatibleLines = BuildCompatibleLines();
        if (compatibleLines.Count == 0)
        {
            return new ReadyHuBindingReadModel();
        }

        var candidatesResult = new HuReservationCandidatesService(_dataStore).Build(new HuReservationCandidatesQuery
        {
            OrderId = 0,
            Lines = compatibleLines
                .Select(line => new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = $"order-line-{line.Line.OrderLineId}",
                    OrderLineId = line.Line.OrderLineId,
                    ItemId = line.Line.ItemId,
                    QtyOrdered = line.Line.MaxAdditionalBindQty
                })
                .ToArray(),
            ExcludeHuCodes = Array.Empty<string>()
        });

        var reservedHu = _dataStore.GetReservedOrderReceiptHuCodes(null)
            .Select(NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compatibleLinesByItem = compatibleLines
            .GroupBy(line => line.Line.ItemId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var itemNames = compatibleLinesByItem.Keys
            .ToDictionary(itemId => itemId, ResolveItemName);
        var locationDisplayByHuItem = BuildLocationDisplayByHuItem();
        var originByHuItem = BuildOriginByHuItem();

        var huRows = candidatesResult.Lines
            .SelectMany(line => line.Candidates.Select(candidate => new CandidateContext(line.ItemId, candidate)))
            .Where(candidate => string.Equals(candidate.Candidate.Source, OrderHuReservationApplyService.SourceLedgerStock, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.Candidate.Qty > QtyTolerance)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Candidate.HuCode))
            .Where(candidate => !candidate.Candidate.ReservedByOrderId.HasValue)
            .Where(candidate => !reservedHu.Contains(NormalizeHu(candidate.Candidate.HuCode) ?? string.Empty))
            .GroupBy(candidate => (candidate.ItemId, HuCode: NormalizeHu(candidate.Candidate.HuCode)!), CandidateKeyComparer.Instance)
            .Select(group => BuildHuRow(
                group.First(),
                compatibleLinesByItem,
                itemNames,
                locationDisplayByHuItem,
                originByHuItem))
            .Where(row => row.CompatibleOrders.Count > 0)
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.FirstReceiptAt ?? DateTime.MaxValue)
            .ThenBy(row => row.FirstReceiptDocId ?? long.MaxValue)
            .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ReadyHuBindingReadModel { HuRows = huRows };
    }

    private IReadOnlyList<CompatibleLineContext> BuildCompatibleLines()
    {
        var result = new List<CompatibleLineContext>();
        foreach (var order in _dataStore.GetOrders()
                     .Where(order => order.Type == OrderType.Customer)
                     .Where(order => order.Status is OrderStatus.InProgress or OrderStatus.Accepted)
                     .OrderBy(order => order.DueDate ?? DateTime.MaxValue)
                     .ThenBy(order => order.CreatedAt)
                     .ThenBy(order => order.OrderRef, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(order => order.Id))
        {
            var shipmentRemainingByLine = _dataStore.GetOrderShipmentRemaining(order.Id)
                .ToDictionary(line => line.OrderLineId);
            if (shipmentRemainingByLine.Count == 0)
            {
                continue;
            }

            var currentBoundByLine = _dataStore.GetOrderReceiptPlanLines(order.Id)
                .Where(line => line.QtyPlanned > QtyTolerance)
                .Where(line => !string.IsNullOrWhiteSpace(line.ToHu))
                .GroupBy(line => line.OrderLineId)
                .ToDictionary(group => group.Key, group => group.ToArray());

            foreach (var orderLine in _dataStore.GetOrderLines(order.Id).OrderBy(line => line.Id))
            {
                if (orderLine.ItemId <= 0 || !shipmentRemainingByLine.TryGetValue(orderLine.Id, out var shipmentLine))
                {
                    continue;
                }

                currentBoundByLine.TryGetValue(orderLine.Id, out var currentBound);
                currentBound ??= Array.Empty<OrderReceiptPlanLine>();
                var currentBoundQty = currentBound.Sum(line => Math.Max(0, line.QtyPlanned));
                var maxAdditional = Math.Max(0, shipmentLine.QtyRemaining - currentBoundQty);
                if (maxAdditional <= QtyTolerance)
                {
                    continue;
                }

                result.Add(new CompatibleLineContext(
                    order,
                    new ReadyHuBindingCompatibleLineRow
                    {
                        OrderLineId = orderLine.Id,
                        ItemId = orderLine.ItemId,
                        ItemName = ResolveItemName(orderLine.ItemId),
                        QtyOrdered = orderLine.QtyOrdered,
                        QtyShipped = shipmentLine.QtyShipped,
                        ShipmentRemainingQty = shipmentLine.QtyRemaining,
                        CurrentBoundHuCodes = currentBound
                            .Select(line => NormalizeHu(line.ToHu))
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Cast<string>()
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        CurrentBoundQty = currentBoundQty,
                        MaxAdditionalBindQty = maxAdditional
                    }));
            }
        }

        return result;
    }

    private ReadyHuBindingHuRow BuildHuRow(
        CandidateContext candidate,
        IReadOnlyDictionary<long, CompatibleLineContext[]> compatibleLinesByItem,
        IReadOnlyDictionary<long, string> itemNames,
        IReadOnlyDictionary<(long ItemId, string HuCode), string> locationDisplayByHuItem,
        IReadOnlyDictionary<(long ItemId, string HuCode), HuOrderContextRow> originByHuItem)
    {
        var huCode = NormalizeHu(candidate.Candidate.HuCode) ?? string.Empty;
        var compatibleOrders = compatibleLinesByItem.TryGetValue(candidate.ItemId, out var lines)
            ? lines
                .Where(line => candidate.Candidate.Qty <= line.Line.MaxAdditionalBindQty + QtyTolerance)
                .GroupBy(line => line.Order.Id)
                .Select(group =>
                {
                    var order = group.First().Order;
                    return new ReadyHuBindingCompatibleOrderRow
                    {
                        OrderId = order.Id,
                        OrderRef = order.OrderRef,
                        PartnerId = order.PartnerId,
                        PartnerName = order.PartnerName,
                        PartnerCode = order.PartnerCode,
                        DueDate = order.DueDate,
                        CreatedAt = order.CreatedAt,
                        Status = OrderStatusMapper.StatusToString(order.Status),
                        Lines = group
                            .Select(line => line.Line)
                            .OrderBy(line => line.OrderLineId)
                            .ToArray()
                    };
                })
                .OrderBy(order => order.DueDate ?? DateTime.MaxValue)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.OrderRef, StringComparer.OrdinalIgnoreCase)
                .ThenBy(order => order.OrderId)
                .ToArray()
            : Array.Empty<ReadyHuBindingCompatibleOrderRow>();

        var key = (candidate.ItemId, huCode);
        originByHuItem.TryGetValue(key, out var origin);
        locationDisplayByHuItem.TryGetValue(key, out var locationDisplay);
        return new ReadyHuBindingHuRow
        {
            HuCode = huCode,
            ItemId = candidate.ItemId,
            ItemName = itemNames.TryGetValue(candidate.ItemId, out var itemName) ? itemName : ResolveItemName(candidate.ItemId),
            Qty = candidate.Candidate.Qty,
            Source = candidate.Candidate.Source,
            LocationDisplay = locationDisplay ?? string.Empty,
            OriginInternalOrderId = origin?.OriginInternalOrderId,
            OriginInternalOrderRef = origin?.OriginInternalOrderRef,
            FirstReceiptAt = candidate.Candidate.FirstReceiptAt,
            FirstReceiptDocId = candidate.Candidate.FirstReceiptDocId,
            CompatibleOrders = compatibleOrders
        };
    }

    private IReadOnlyDictionary<(long ItemId, string HuCode), string> BuildLocationDisplayByHuItem()
    {
        var locations = _dataStore.GetLocations().ToDictionary(location => location.Id);
        return _dataStore.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => (row.ItemId, HuCode: NormalizeHu(row.HuCode)!))
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    "; ",
                    group.OrderBy(row => locations.TryGetValue(row.LocationId, out var location) ? location.Code : row.LocationId.ToString())
                        .Select(row =>
                        {
                            var locationName = locations.TryGetValue(row.LocationId, out var location)
                                ? string.IsNullOrWhiteSpace(location.Code) ? location.Name : location.Code
                                : row.LocationId.ToString();
                            return $"{locationName}: {row.Qty:0.###}";
                        })),
                CandidateKeyComparer.Instance);
    }

    private IReadOnlyDictionary<(long ItemId, string HuCode), HuOrderContextRow> BuildOriginByHuItem()
    {
        return _dataStore.GetHuOrderContextRows()
            .Where(row => row.OriginInternalOrderId.HasValue || !string.IsNullOrWhiteSpace(row.OriginInternalOrderRef))
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => (row.ItemId, HuCode: NormalizeHu(row.HuCode)!))
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                CandidateKeyComparer.Instance);
    }

    private string ResolveItemName(long itemId)
    {
        var item = _dataStore.FindItemById(itemId);
        return string.IsNullOrWhiteSpace(item?.Name) ? $"Товар {itemId}" : item.Name;
    }

    private static string? NormalizeHu(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private sealed record CompatibleLineContext(Order Order, ReadyHuBindingCompatibleLineRow Line);

    private sealed record CandidateContext(long ItemId, HuReservationCandidateResult Candidate);

    private sealed class CandidateKeyComparer : IEqualityComparer<(long ItemId, string HuCode)>
    {
        public static readonly CandidateKeyComparer Instance = new();

        public bool Equals((long ItemId, string HuCode) x, (long ItemId, string HuCode) y) =>
            x.ItemId == y.ItemId && string.Equals(x.HuCode, y.HuCode, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((long ItemId, string HuCode) obj) =>
            HashCode.Combine(obj.ItemId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.HuCode));
    }
}
