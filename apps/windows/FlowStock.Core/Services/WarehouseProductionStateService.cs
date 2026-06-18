using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class WarehouseProductionStateService(IDataStore dataStore)
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<WarehouseProductionStateRow> GetRows(
        bool includeZero = false,
        string? search = null,
        bool belowMinOnly = false)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var items = _dataStore.GetItems(null);
        var itemsById = items.ToDictionary(item => item.Id);
        var locationsById = _dataStore.GetLocations().ToDictionary(location => location.Id);
        var stockRows = _dataStore.GetStock(null);
        var huContextByKey = BuildHuContextMap(_dataStore.GetHuOrderContextRows());
        var huRows = _dataStore.GetHuStockRows();
        var needRows = new ProductionNeedService(_dataStore).GetRows(includeZeroNeed: true)
            .ToDictionary(row => row.ItemId);
        var optimizedStore = _dataStore as IOptimizedWarehouseProductionStateStore;

        var stockByItem = stockRows
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.First();
                    var stockQty = group.Sum(row => row.Qty);
                    return new StockAggregate(
                        stockQty,
                        first.ReservedCustomerOrderQty,
                        stockQty - first.ReservedCustomerOrderQty);
                });

        var huRowsByItem = BuildHuRowsByItem(huRows, huContextByKey, locationsById);
        var customerOrdersByItem = optimizedStore?.GetWarehouseProductionStateCustomerOrdersByItem()
                                   ?? BuildCustomerOrdersByItem();
        var internalOrdersByItem = optimizedStore?.GetWarehouseProductionStateInternalOrdersByItem()
                                   ?? BuildInternalOrdersByItem();
        var palletByItem = optimizedStore?.GetWarehouseProductionStatePalletsByItem()
                           ?? BuildPalletRowsByItem();

        var itemIds = new HashSet<long>(itemsById.Keys);
        itemIds.UnionWith(stockByItem.Keys);
        itemIds.UnionWith(needRows.Keys);
        itemIds.UnionWith(customerOrdersByItem.Keys);
        itemIds.UnionWith(internalOrdersByItem.Keys);
        itemIds.UnionWith(palletByItem.Keys);

        var rows = new List<WarehouseProductionStateRow>();
        foreach (var itemId in itemIds)
        {
            itemsById.TryGetValue(itemId, out var item);
            stockByItem.TryGetValue(itemId, out var stock);
            needRows.TryGetValue(itemId, out var need);
            huRowsByItem.TryGetValue(itemId, out var itemHuRows);
            customerOrdersByItem.TryGetValue(itemId, out var customerOrders);
            internalOrdersByItem.TryGetValue(itemId, out var internalOrders);
            palletByItem.TryGetValue(itemId, out var palletAggregate);

            itemHuRows ??= Array.Empty<WarehouseProductionStateHuRow>();
            customerOrders ??= Array.Empty<WarehouseProductionStateCustomerOrderRow>();
            internalOrders ??= Array.Empty<WarehouseProductionStateInternalOrderRow>();
            palletAggregate ??= WarehouseProductionStatePalletAggregate.Empty;

            var minStockQty = need?.MinStockQty
                              ?? (item?.ItemTypeEnableMinStockControl == true && (item.MinStockQty ?? 0) > QtyTolerance
                                  ? item.MinStockQty!.Value
                                  : 0d);
            var stockQty = stock?.StockQty ?? 0d;
            var reservedQty = stock?.ReservedQty ?? 0d;
            var freeQty = stock?.FreeQty ?? (stockQty - reservedQty);
            var belowMinQty = minStockQty > QtyTolerance ? Math.Max(0d, minStockQty - freeQty) : 0d;
            var internalOpenQty = need?.OpenInternalOrderQty ?? internalOrders.Sum(row => row.RemainingQty);
            var internalRemainingQty = internalOpenQty;
            var customerRemainingToShipQty = customerOrders.Sum(row => row.RemainingQty);
            var rawCustomerOpenDemandQty = need?.ToCloseOrdersQty ?? 0d;
            var customerOpenDemandQty = customerRemainingToShipQty > QtyTolerance
                ? Math.Min(rawCustomerOpenDemandQty, customerRemainingToShipQty)
                : 0d;
            var rawRemainingNeedQty = need?.TotalToMakeQty ?? Math.Max(0d, rawCustomerOpenDemandQty + belowMinQty - internalOpenQty);
            var remainingNeedQty = Math.Max(0d, rawRemainingNeedQty - rawCustomerOpenDemandQty + customerOpenDemandQty);
            var warnings = BuildWarnings(
                belowMinQty,
                customerOpenDemandQty,
                internalRemainingQty,
                palletAggregate.PlannedQty,
                palletAggregate.HasFilledWithoutLedger,
                palletAggregate.HasStalePalletAfterFullShipment);

            var row = new WarehouseProductionStateRow
            {
                ItemId = itemId,
                ItemName = item?.Name ?? need?.ItemName ?? $"#{itemId}",
                Barcode = item?.Barcode,
                Gtin = item?.Gtin ?? need?.Gtin,
                ItemType = item?.ItemTypeName ?? need?.ItemTypeName,
                Brand = item?.Brand,
                BaseUom = string.IsNullOrWhiteSpace(item?.BaseUom) ? "шт" : item!.BaseUom,
                StockQty = stockQty,
                FreeQty = freeQty,
                ReservedQty = reservedQty,
                MinStockQty = minStockQty,
                BelowMinQty = belowMinQty,
                CustomerOpenDemandQty = customerOpenDemandQty,
                CustomerRemainingToShipQty = customerRemainingToShipQty,
                InternalOpenQty = internalOpenQty,
                InternalRemainingQty = internalRemainingQty,
                PrdPlannedQty = palletAggregate.PlannedQty,
                PrdFilledQty = palletAggregate.FilledQty,
                PalletPlannedCount = palletAggregate.PlannedCount,
                PalletFilledCount = palletAggregate.FilledCount,
                RemainingNeedQty = remainingNeedQty,
                NeedReason = need?.Reason ?? BuildFallbackNeedReason(belowMinQty, customerOpenDemandQty, internalRemainingQty),
                Warnings = warnings,
                NeedBreakdown = new WarehouseProductionStateNeedBreakdownRow
                {
                    DemandToCloseCustomerOrders = customerOpenDemandQty,
                    DemandToMinStock = belowMinQty,
                    AlreadyPlannedInternal = internalOpenQty,
                    AlreadyPlannedPrd = palletAggregate.PlannedQty,
                    RemainingToCreate = remainingNeedQty
                },
                HuRows = itemHuRows,
                CustomerOrders = customerOrders,
                InternalOrders = internalOrders,
                ProductionReceipts = palletAggregate.Rows
            };

            if (!MatchesSearch(row, normalizedSearch))
            {
                continue;
            }

            if (belowMinOnly && row.BelowMinQty <= QtyTolerance)
            {
                continue;
            }

            if (!includeZero && !HasActivity(row))
            {
                continue;
            }

            rows.Add(row);
        }

        return rows
            .OrderByDescending(row => row.RemainingNeedQty)
            .ThenByDescending(row => row.BelowMinQty)
            .ThenByDescending(row => row.PrdPlannedQty)
            .ThenBy(row => row.ItemType ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();
    }

    private Dictionary<long, IReadOnlyList<WarehouseProductionStateHuRow>> BuildHuRowsByItem(
        IReadOnlyList<HuStockRow> huRows,
        IReadOnlyDictionary<string, HuOrderContextRow> huContextByKey,
        IReadOnlyDictionary<long, Location> locationsById)
    {
        return huRows
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WarehouseProductionStateHuRow>)group
                    .OrderBy(row => locationsById.TryGetValue(row.LocationId, out var location) ? location.Code : string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                    .Select(row =>
                    {
                        huContextByKey.TryGetValue(BuildHuItemKey(row.ItemId, row.HuCode), out var context);
                        var locationDisplay = locationsById.TryGetValue(row.LocationId, out var location)
                            ? string.IsNullOrWhiteSpace(location.Name)
                                ? location.Code
                                : $"{location.Code} — {location.Name}"
                            : row.LocationId.ToString();
                        return new WarehouseProductionStateHuRow
                        {
                            Location = locationDisplay,
                            LocationId = row.LocationId,
                            HuCode = row.HuCode,
                            Qty = row.Qty,
                            OriginInternalOrderId = context?.OriginInternalOrderId,
                            OriginInternalOrderRef = context?.OriginInternalOrderRef,
                            ReservedCustomerOrderId = context?.ReservedCustomerOrderId,
                            ReservedCustomerOrderRef = context?.ReservedCustomerOrderRef,
                            ReservedCustomerId = context?.ReservedCustomerId,
                            ReservedCustomerName = context?.ReservedCustomerName,
                            StockStatus = WarehouseProductionStatePresentation.BuildWarehouseHuStatus(
                                row.Qty,
                                context?.ReservedCustomerOrderRef,
                                context?.ReservedCustomerName)
                        };
                    })
                    .ToList());
    }

    private Dictionary<long, IReadOnlyList<WarehouseProductionStateCustomerOrderRow>> BuildCustomerOrdersByItem()
    {
        var result = new Dictionary<long, List<WarehouseProductionStateCustomerOrderRow>>();
        var activeOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Customer
                            && order.Status is not (OrderStatus.Draft or OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged));

        foreach (var order in activeOrders)
        {
            var lines = _dataStore.GetOrderLines(order.Id);
            var shippedByLine = _dataStore.GetShippedTotalsByOrderLine(order.Id);

            foreach (var lineGroup in lines.GroupBy(line => line.ItemId))
            {
                var qtyOrdered = lineGroup.Sum(line => line.QtyOrdered);
                var shippedQty = lineGroup.Sum(line => shippedByLine.TryGetValue(line.Id, out var shipped) ? shipped : 0d);
                var remainingQty = lineGroup.Sum(line => Math.Max(0d, line.QtyOrdered - (shippedByLine.TryGetValue(line.Id, out var shipped) ? shipped : 0d)));
                if (remainingQty <= QtyTolerance)
                {
                    continue;
                }

                if (!result.TryGetValue(lineGroup.Key, out var bucket))
                {
                    bucket = new List<WarehouseProductionStateCustomerOrderRow>();
                    result[lineGroup.Key] = bucket;
                }

                bucket.Add(new WarehouseProductionStateCustomerOrderRow
                {
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    PartnerName = order.PartnerName,
                    Status = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
                    QtyOrdered = qtyOrdered,
                    ShippedQty = shippedQty,
                    RemainingQty = remainingQty
                });
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<WarehouseProductionStateCustomerOrderRow>)pair.Value
                .OrderBy(row => row.OrderRef, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private Dictionary<long, IReadOnlyList<WarehouseProductionStateInternalOrderRow>> BuildInternalOrdersByItem()
    {
        var result = new Dictionary<long, List<WarehouseProductionStateInternalOrderRow>>();
        var activeOrders = _dataStore.GetOrders()
            .Where(order => order.Type == OrderType.Internal
                            && order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged));

        foreach (var order in activeOrders)
        {
            var lines = _dataStore.GetOrderLines(order.Id);
            var remainingByLine = OrderReceiptRemainingCalculator.GetRemaining(_dataStore, order)
                .ToDictionary(line => line.OrderLineId);

            foreach (var lineGroup in lines.GroupBy(line => line.ItemId))
            {
                var qtyOrdered = lineGroup.Sum(line => line.QtyOrdered);
                var remainingQty = lineGroup.Sum(line =>
                {
                    return remainingByLine.TryGetValue(line.Id, out var remaining)
                        ? Math.Max(0d, remaining.QtyRemaining)
                        : Math.Max(0d, line.QtyOrdered);
                });
                if (remainingQty <= QtyTolerance)
                {
                    continue;
                }

                if (!result.TryGetValue(lineGroup.Key, out var bucket))
                {
                    bucket = new List<WarehouseProductionStateInternalOrderRow>();
                    result[lineGroup.Key] = bucket;
                }

                bucket.Add(new WarehouseProductionStateInternalOrderRow
                {
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    Status = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
                    QtyOrdered = qtyOrdered,
                    ProducedQty = Math.Max(0d, qtyOrdered - remainingQty),
                    RemainingQty = remainingQty
                });
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<WarehouseProductionStateInternalOrderRow>)pair.Value
                .OrderBy(row => row.OrderRef, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private Dictionary<long, WarehouseProductionStatePalletAggregate> BuildPalletRowsByItem()
    {
        var result = new Dictionary<long, PallettAggregate>();
        var ordersById = new Dictionary<long, Order?>();

        Order? ResolveOrder(long? orderId)
        {
            if (!orderId.HasValue)
            {
                return null;
            }

            if (!ordersById.TryGetValue(orderId.Value, out var order))
            {
                order = _dataStore.GetOrder(orderId.Value);
                ordersById[orderId.Value] = order;
            }

            return order;
        }

        foreach (var doc in _dataStore.GetDocs()
                     .Where(doc => doc.Type == DocType.ProductionReceipt
                                   && doc.Status != DocStatus.Closed))
        {
            var pallets = _dataStore.GetProductionPalletsByDoc(doc.Id)
                .Where(pallet => IsOpenProductionPalletStatus(pallet.Status))
                .ToList();

            foreach (var pallet in pallets)
            {
                var effectiveOrderId = pallet.OrderId ?? doc.OrderId;
                var effectiveOrder = ResolveOrder(effectiveOrderId);
                if (!ShouldIncludeProductionPalletOrder(effectiveOrder))
                {
                    continue;
                }

                var workItem = new ProductionPalletWorkItem
                {
                    PrdDocId = doc.Id,
                    PrdDocRef = doc.DocRef,
                    PrdStatus = DocTypeMapper.StatusToString(doc.Status),
                    OrderId = effectiveOrderId,
                    OrderRef = effectiveOrder?.OrderRef ?? (effectiveOrderId == doc.OrderId ? doc.OrderRef : null)
                };
                var hasStalePalletAfterFullShipment = effectiveOrder?.Type == OrderType.Customer
                                                      && effectiveOrder.Status == OrderStatus.Shipped;

                if (pallet.Lines.Count > 0)
                {
                    foreach (var line in pallet.Lines)
                    {
                        AddPalletRow(result, workItem, pallet, line.ItemId, line.ItemName, line.PlannedQty, EffectiveFilledQty(pallet, line.PlannedQty, line.FilledQty), hasStalePalletAfterFullShipment);
                    }

                    continue;
                }

                AddPalletRow(result, workItem, pallet, pallet.ItemId, pallet.ItemName, pallet.PlannedQty, EffectiveFilledQty(pallet, pallet.PlannedQty, 0d), hasStalePalletAfterFullShipment);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => new WarehouseProductionStatePalletAggregate
            {
                Rows = pair.Value.Rows,
                PlannedQty = pair.Value.PlannedQty,
                FilledQty = pair.Value.FilledQty,
                PlannedCount = pair.Value.PlannedCount,
                FilledCount = pair.Value.FilledCount,
                HasFilledWithoutLedger = pair.Value.HasFilledWithoutLedger,
                HasStalePalletAfterFullShipment = pair.Value.HasStalePalletAfterFullShipment
            });
    }

    private void AddPalletRow(
        IDictionary<long, PallettAggregate> result,
        ProductionPalletWorkItem workItem,
        ProductionPallet pallet,
        long itemId,
        string itemName,
        double plannedQty,
        double filledQty,
        bool hasStalePalletAfterFullShipment)
    {
        var locationText = string.IsNullOrWhiteSpace(pallet.ToLocationCode) ? null : pallet.ToLocationCode;
        var inLedger = pallet.ToLocationId.HasValue
                       && _dataStore.GetLedgerBalance(itemId, pallet.ToLocationId.Value, pallet.HuCode) > QtyTolerance;
        if (inLedger)
        {
            return;
        }

        var prdIsOpen = !string.Equals(
            workItem.PrdStatus,
            DocTypeMapper.StatusToString(DocStatus.Closed),
            StringComparison.OrdinalIgnoreCase);
        var displayQty = WarehouseProductionStatePresentation.ResolvePalletDisplayQty(pallet.Status, plannedQty, filledQty);
        var composition = pallet.IsMixedPallet
            ? string.Join(", ", pallet.Lines.Select(line => $"{line.ItemName} {FormatQty(line.PlannedQty)}"))
            : itemName;
        var row = new WarehouseProductionStatePalletRow
        {
            PrdDocId = workItem.PrdDocId,
            PrdRef = workItem.PrdDocRef,
            PalletId = pallet.Id,
            HuCode = pallet.HuCode,
            PalletStatus = pallet.Status,
            PalletStatusDisplay = WarehouseProductionStatePresentation.MapPalletStatusDisplay(pallet.Status),
            SourceOrderRef = workItem.OrderRef,
            PlannedQty = Math.Max(0d, plannedQty),
            FilledQty = Math.Max(0d, filledQty),
            Qty = displayQty,
            StockEffect = "план / производство",
            StatusNote = WarehouseProductionStatePresentation.BuildPalletStatusNote(pallet.Status, prdIsOpen, inLedger),
            IsMixedPallet = pallet.IsMixedPallet,
            Composition = composition,
            Location = locationText
        };

        var aggregate = result.TryGetValue(itemId, out var current)
            ? current
            : PallettAggregate.Empty;
        var plannedPalletIds = new HashSet<long>(aggregate.PlannedPalletIds);
        var filledPalletIds = new HashSet<long>(aggregate.FilledPalletIds);
        plannedPalletIds.Add(pallet.Id);
        if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            filledPalletIds.Add(pallet.Id);
        }

        result[itemId] = new PallettAggregate(
            aggregate.Rows.Concat([row]).OrderBy(entry => entry.PrdRef, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.HuCode, StringComparer.OrdinalIgnoreCase).ToArray(),
            aggregate.PlannedQty + Math.Max(0d, plannedQty),
            aggregate.FilledQty + Math.Max(0d, filledQty),
            plannedPalletIds,
            filledPalletIds,
            aggregate.HasFilledWithoutLedger || (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase) && !inLedger),
            aggregate.HasStalePalletAfterFullShipment || hasStalePalletAfterFullShipment);
    }

    private static double EffectiveFilledQty(ProductionPallet pallet, double plannedQty, double filledQty)
    {
        if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(0d, filledQty);
        }

        return filledQty > QtyTolerance ? filledQty : Math.Max(0d, plannedQty);
    }

    private static bool ShouldIncludeProductionPalletOrder(Order? order)
    {
        return order == null
               || (order.Type == OrderType.Internal
                   && order.Status is OrderStatus.Draft or OrderStatus.InProgress)
               || (order.Type == OrderType.Customer
                   && order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged));
    }

    private static IReadOnlyList<string> BuildWarnings(
        double belowMinQty,
        double customerOpenDemandQty,
        double internalRemainingQty,
        double prdPlannedQty,
        bool hasFilledWithoutLedger,
        bool hasStalePalletAfterFullShipment)
    {
        var warnings = new List<string>();
        if (belowMinQty > QtyTolerance)
        {
            warnings.Add("BELOW_MIN");
        }

        if (customerOpenDemandQty > QtyTolerance)
        {
            warnings.Add("HAS_CUSTOMER_DEMAND");
        }

        if (internalRemainingQty > QtyTolerance)
        {
            warnings.Add("HAS_OPEN_INTERNAL_PLAN");
        }

        if (prdPlannedQty > QtyTolerance)
        {
            warnings.Add("HAS_OPEN_PALLET_PLAN");
        }

        if (hasFilledWithoutLedger)
        {
            warnings.Add("FILLED_PALLET_WITHOUT_LEDGER");
        }

        if (hasStalePalletAfterFullShipment)
        {
            warnings.Add("STALE_PALLET_AFTER_FULL_SHIPMENT");
        }

        return warnings;
    }

    private static bool MatchesSearch(WarehouseProductionStateRow row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return Contains(row.ItemName, search)
               || Contains(row.Barcode, search)
               || Contains(row.Gtin, search)
               || Contains(row.ItemType, search)
               || Contains(row.Brand, search);
    }

    private static bool HasActivity(WarehouseProductionStateRow row)
    {
        return Math.Abs(row.StockQty) > QtyTolerance
               || Math.Abs(row.ReservedQty) > QtyTolerance
               || Math.Abs(row.CustomerOpenDemandQty) > QtyTolerance
               || Math.Abs(row.CustomerRemainingToShipQty) > QtyTolerance
               || Math.Abs(row.InternalRemainingQty) > QtyTolerance
               || Math.Abs(row.PrdPlannedQty) > QtyTolerance
               || Math.Abs(row.PrdFilledQty) > QtyTolerance
               || Math.Abs(row.BelowMinQty) > QtyTolerance;
    }

    private static string BuildFallbackNeedReason(double belowMinQty, double customerOpenDemandQty, double internalRemainingQty)
    {
        if (customerOpenDemandQty > QtyTolerance)
        {
            return "Есть открытый спрос клиентских заказов.";
        }

        if (belowMinQty > QtyTolerance && internalRemainingQty > QtyTolerance)
        {
            return "Требуется пополнение склада, часть уже покрыта внутренним планом.";
        }

        if (belowMinQty > QtyTolerance)
        {
            return "Требуется пополнение склада до минимального остатка.";
        }

        if (internalRemainingQty > QtyTolerance)
        {
            return "Есть открытый внутренний план выпуска.";
        }

        return string.Empty;
    }

    private static bool Contains(string? source, string search)
    {
        return !string.IsNullOrWhiteSpace(source)
               && source.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IReadOnlyDictionary<string, HuOrderContextRow> BuildHuContextMap(IReadOnlyList<HuOrderContextRow> rows)
    {
        return (rows ?? Array.Empty<HuOrderContextRow>())
            .Where(row => row.ItemId > 0 && !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => BuildHuItemKey(row.ItemId, row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildHuItemKey(long itemId, string huCode)
    {
        return $"{itemId}|{huCode.Trim().ToUpperInvariant()}";
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###");
    }

    private static bool IsOpenProductionPalletStatus(string? status)
    {
        return string.Equals(status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StockAggregate(double StockQty, double ReservedQty, double FreeQty);

    private sealed record PallettAggregate(
        IReadOnlyList<WarehouseProductionStatePalletRow> Rows,
        double PlannedQty,
        double FilledQty,
        IReadOnlySet<long> PlannedPalletIds,
        IReadOnlySet<long> FilledPalletIds,
        bool HasFilledWithoutLedger,
        bool HasStalePalletAfterFullShipment)
    {
        public static PallettAggregate Empty { get; } = new(
            Array.Empty<WarehouseProductionStatePalletRow>(),
            0d,
            0d,
            new HashSet<long>(),
            new HashSet<long>(),
            false,
            false);

        public int PlannedCount => PlannedPalletIds.Count;
        public int FilledCount => FilledPalletIds.Count;
    }
}
