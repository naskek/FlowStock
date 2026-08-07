using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Read-model экрана «Управление привязками складских HU». Тяжёлые складские выборки
/// (товары, HU выбранного товара) делегируются оптимизированному
/// <see cref="IHuBindingManagementReadStore"/> (SQL-фильтрация по товару). Для целевых строк
/// доступная ёмкость (<c>max_additional_bind_qty</c>) рассчитывается тем же безопасным
/// способом, что и в существующем уведомлении о свободных HU, переиспользуя
/// <see cref="OrderReceiptRemainingCalculator"/> и открытые производственные паллеты.
/// </summary>
public sealed class HuBindingManageReadModelService
{
    private const double QtyTolerance = StockQuantityRules.QtyTolerance;
    private const int MaxItemsLimit = 500;
    private const int MaxHuLimit = 500;

    private readonly IDataStore _dataStore;
    private readonly IHuBindingManagementReadStore _readStore;

    public HuBindingManageReadModelService(IDataStore dataStore)
    {
        _dataStore = dataStore;
        _readStore = dataStore as IHuBindingManagementReadStore
                     ?? throw new InvalidOperationException("Хранилище не поддерживает read-model управления привязками HU.");
    }

    public IReadOnlyList<HuBindingManageItemRow> GetItems(string? search, int limit)
    {
        var normalizedLimit = NormalizeLimit(limit, MaxItemsLimit);
        var coarseItems = _readStore.GetManagementItems(NormalizeSearch(search), int.MaxValue);
        if (_dataStore is not IHuOperatorFactsStore factsStore || coarseItems.Count == 0)
        {
            return coarseItems.Take(normalizedLimit).ToArray();
        }

        var huByItem = _dataStore.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => (row.ItemId, HuCode: NormalizeHu(row.HuCode)))
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .Select(row => (row.ItemId, HuCode: row.HuCode!))
            .Distinct()
            .ToArray();
        var decisions = EvaluateBindingEligibility(factsStore, huByItem.Select(row => row.HuCode));
        var eligibleCountByItem = huByItem
            .Where(row => decisions.TryGetValue(row.HuCode, out var decision) && decision.Allowed)
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        return coarseItems
            .Where(item => eligibleCountByItem.ContainsKey(item.ItemId))
            .Select(item => new HuBindingManageItemRow
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                HuCount = eligibleCountByItem[item.ItemId]
            })
            .Take(normalizedLimit)
            .ToArray();
    }

    public HuBindingManageHuPage GetHuRows(long itemId, HuBindingManageHuFilter filter)
    {
        var normalized = new HuBindingManageHuFilter
        {
            HuSearch = NormalizeSearch(filter.HuSearch),
            OrderSearch = NormalizeSearch(filter.OrderSearch),
            PartnerSearch = NormalizeSearch(filter.PartnerSearch),
            State = filter.State,
            Limit = NormalizeLimit(filter.Limit, MaxHuLimit),
            Offset = Math.Max(0, filter.Offset)
        };
        if (_dataStore is not IHuOperatorFactsStore factsStore)
        {
            return _readStore.GetManagementHuRows(itemId, normalized);
        }

        var coarseRows = new List<HuBindingManageHuRow>();
        var coarseOffset = 0;
        string itemName = string.Empty;
        while (true)
        {
            var coarsePage = _readStore.GetManagementHuRows(itemId, new HuBindingManageHuFilter
            {
                HuSearch = normalized.HuSearch,
                OrderSearch = normalized.OrderSearch,
                PartnerSearch = normalized.PartnerSearch,
                State = normalized.State,
                Limit = MaxHuLimit,
                Offset = coarseOffset
            });
            itemName = string.IsNullOrWhiteSpace(itemName) ? coarsePage.ItemName : itemName;
            coarseRows.AddRange(coarsePage.HuRows);
            coarseOffset += coarsePage.HuRows.Count;
            if (coarsePage.HuRows.Count == 0 || coarseOffset >= coarsePage.Total)
            {
                break;
            }
        }

        var decisions = EvaluateBindingEligibility(factsStore, coarseRows.Select(row => row.HuCode));
        var eligibleRows = coarseRows
            .Where(row => decisions.TryGetValue(row.HuCode, out var decision) && decision.Allowed)
            .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HuBindingManageHuPage
        {
            ItemId = itemId,
            ItemName = itemName,
            Total = eligibleRows.Length,
            Limit = normalized.Limit,
            Offset = normalized.Offset,
            HuRows = eligibleRows.Skip(normalized.Offset).Take(normalized.Limit).ToArray()
        };
    }

    public IReadOnlyList<HuBindingManageTargetLine> GetTargets(long itemId)
    {
        var rows = _readStore.GetManagementTargetLines(itemId);
        if (rows.Count == 0)
        {
            return Array.Empty<HuBindingManageTargetLine>();
        }

        var result = new List<HuBindingManageTargetLine>(rows.Count);
        foreach (var orderGroup in rows.GroupBy(row => row.OrderId))
        {
            var orderId = orderGroup.Key;
            var orderLines = _dataStore.GetOrderLines(orderId);
            var producedByLine = OrderReceiptRemainingCalculator
                .BuildConfirmedReceiptLedgerTotalsByOrderLine(_dataStore, orderId, orderLines);
            var openPalletQtyByLine = BuildOpenProductionPalletQtyByOrderLine(orderId);
            var shipmentByLine = _dataStore.GetOrderShipmentRemaining(orderId)
                .ToDictionary(line => line.OrderLineId);

            foreach (var row in orderGroup)
            {
                var producedQty = producedByLine.TryGetValue(row.OrderLineId, out var produced) ? Math.Max(0, produced) : 0d;
                var openPalletQty = openPalletQtyByLine.TryGetValue(row.OrderLineId, out var openQty) ? Math.Max(0, openQty) : 0d;
                var maxAdditional = Math.Max(0, row.QtyOrdered - producedQty - openPalletQty - row.CurrentBoundQty);
                var qtyShipped = shipmentByLine.TryGetValue(row.OrderLineId, out var shipmentLine)
                    ? shipmentLine.QtyShipped
                    : row.QtyShipped;

                result.Add(new HuBindingManageTargetLine
                {
                    OrderId = row.OrderId,
                    OrderRef = row.OrderRef,
                    PartnerName = row.PartnerName,
                    OrderStatus = row.OrderStatus,
                    DueAt = row.DueAt,
                    OrderLineId = row.OrderLineId,
                    ItemId = row.ItemId,
                    QtyOrdered = row.QtyOrdered,
                    QtyShipped = qtyShipped,
                    CurrentBoundHuCodes = row.CurrentBoundHuCodes,
                    CurrentBoundQty = row.CurrentBoundQty,
                    MaxAdditionalBindQty = maxAdditional
                });
            }
        }

        return result
            .OrderBy(line => line.DueAt ?? DateTime.MaxValue)
            .ThenBy(line => line.OrderRef, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.OrderId)
            .ThenBy(line => line.OrderLineId)
            .ToArray();
    }

    private IReadOnlyDictionary<long, double> BuildOpenProductionPalletQtyByOrderLine(long orderId)
    {
        var totals = new Dictionary<long, double>();
        foreach (var doc in _dataStore.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed))
        {
            foreach (var pallet in _dataStore.GetProductionPalletsByDoc(doc.Id).Where(IsOpenProductionPallet))
            {
                if (pallet.Lines.Count > 0)
                {
                    foreach (var line in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                    {
                        AddQty(totals, line.OrderLineId!.Value, line.PlannedQty);
                    }

                    continue;
                }

                if (pallet.OrderLineId.HasValue)
                {
                    AddQty(totals, pallet.OrderLineId.Value, pallet.PlannedQty);
                }
            }
        }

        return totals;
    }

    private static bool IsOpenProductionPallet(ProductionPallet pallet) =>
        string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
        || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase);

    private static void AddQty(IDictionary<long, double> totals, long orderLineId, double qty)
    {
        if (qty <= QtyTolerance)
        {
            return;
        }

        totals[orderLineId] = totals.TryGetValue(orderLineId, out var current) ? current + qty : qty;
    }

    private static string? NormalizeSearch(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    private static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private static IReadOnlyDictionary<string, HuMutationEligibilityDecision> EvaluateBindingEligibility(
        IHuOperatorFactsStore factsStore,
        IEnumerable<string> huCodes)
    {
        return new HuMutationEligibilityService(factsStore).Evaluate(
            huCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            facts =>
            {
                var activeTargets = facts.Reservations
                    .Where(reservation => reservation.Qty > QtyTolerance)
                    .Where(reservation => string.Equals(reservation.OrderType, "CUSTOMER", StringComparison.OrdinalIgnoreCase))
                    .Where(reservation => !string.Equals(reservation.OrderStatus, "SHIPPED", StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(reservation.OrderStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(reservation.OrderStatus, "MERGED", StringComparison.OrdinalIgnoreCase))
                    .Select(reservation => reservation.OrderId)
                    .Distinct()
                    .ToArray();
                return HuMutationEligibilityService.WholeStockContext(
                    facts,
                    HuMutationOperation.ReserveOrBind,
                    activeTargets.Length == 1 ? activeTargets[0] : null);
            });
    }

    private static int NormalizeLimit(int limit, int max)
    {
        if (limit <= 0)
        {
            return max;
        }

        return Math.Min(limit, max);
    }
}
