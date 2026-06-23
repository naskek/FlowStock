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
        return _readStore.GetManagementItems(NormalizeSearch(search), normalizedLimit);
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
        return _readStore.GetManagementHuRows(itemId, normalized);
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

    private static int NormalizeLimit(int limit, int max)
    {
        if (limit <= 0)
        {
            return max;
        }

        return Math.Min(limit, max);
    }
}
