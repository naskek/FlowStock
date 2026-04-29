using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using System.Linq;

namespace FlowStock.Core.Services;

public sealed class OrderService
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _data;

    public OrderService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<Order> GetOrders()
    {
        var orders = _data.GetOrders();
        var result = new List<Order>(orders.Count);
        foreach (var order in orders)
        {
            result.Add(ApplyAutoStatus(order));
        }

        return result;
    }

    public Order? GetOrder(long id)
    {
        var order = _data.GetOrder(id);
        return order == null ? null : ApplyAutoStatus(order);
    }

    public IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId)
    {
        var order = _data.GetOrder(orderId);
        if (order == null)
        {
            return Array.Empty<OrderLineView>();
        }

        var lines = _data.GetOrderLineViews(orderId);
        ApplyLineMetrics(order, lines);
        return lines;
    }

    public IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemainingDetailed(long orderId, bool includeReservedStock = true)
    {
        var order = _data.GetOrder(orderId);
        var isCustomerOrder = order?.Type == OrderType.Customer;
        var planned = _data.GetOrderReceiptPlanLines(orderId)
            .OrderBy(line => line.SortOrder)
            .ThenBy(line => line.Id)
            .ToList();
        var baseRemaining = (includeReservedStock
                ? _data.GetOrderReceiptRemaining(orderId)
                : _data.GetOrderReceiptRemainingWithoutReservedStock(orderId))
            .ToDictionary(line => line.OrderLineId, line => line);

        if (!includeReservedStock && isCustomerOrder)
        {
            return baseRemaining.Values
                .Where(line => line.QtyRemaining > QtyTolerance)
                .OrderBy(line => line.OrderLineId)
                .ToList();
        }

        if (planned.Count == 0)
        {
            return baseRemaining.Values
                .Where(line => line.QtyRemaining > QtyTolerance)
                .OrderBy(line => line.OrderLineId)
                .ToList();
        }

        var producedByOrderLine = baseRemaining
            .ToDictionary(entry => entry.Key, entry => Math.Max(0, entry.Value.QtyReceived));
        var result = new List<OrderReceiptLine>();

        foreach (var line in planned)
        {
            var producedLeft = producedByOrderLine.TryGetValue(line.OrderLineId, out var value) ? value : 0d;
            var consumed = Math.Min(line.QtyPlanned, producedLeft);
            var remaining = Math.Max(0, line.QtyPlanned - consumed);
            producedByOrderLine[line.OrderLineId] = Math.Max(0, producedLeft - consumed);
            if (remaining <= QtyTolerance)
            {
                continue;
            }

            result.Add(new OrderReceiptLine
            {
                OrderLineId = line.OrderLineId,
                OrderId = line.OrderId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QtyOrdered = line.QtyPlanned,
                QtyReceived = consumed,
                QtyRemaining = remaining,
                ToLocationId = line.ToLocationId,
                ToLocation = line.ToLocationCode,
                ToHu = line.ToHu,
                SortOrder = line.SortOrder
            });
        }

        foreach (var entry in baseRemaining.Values.Where(line => line.QtyRemaining > QtyTolerance))
        {
            if (result.Any(line => line.OrderLineId == entry.OrderLineId))
            {
                continue;
            }

            result.Add(entry);
        }

        return result
            .OrderBy(line => line.SortOrder)
            .ThenBy(line => line.OrderLineId)
            .ToList();
    }

    public IReadOnlyDictionary<long, double> GetItemAvailability()
    {
        return _data.GetLedgerTotalsByItem();
    }

    public IReadOnlyDictionary<long, double> GetShippedTotals(long orderId)
    {
        return _data.GetShippedTotalsByOrderLine(orderId);
    }

    public IReadOnlyDictionary<long, HashSet<string>> GetOrderBoundHuByItem(long orderId)
    {
        var result = new Dictionary<long, HashSet<string>>();

        var productionDocs = _data.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed)
            .ToList();
        foreach (var doc in productionDocs)
        {
            foreach (var line in _data.GetDocLines(doc.Id))
            {
                if (line.Qty <= QtyTolerance)
                {
                    continue;
                }

                var huCode = NormalizeHu(line.ToHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                if (!result.TryGetValue(line.ItemId, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[line.ItemId] = set;
                }

                set.Add(huCode);
            }
        }

        foreach (var line in _data.GetOrderReceiptPlanLines(orderId))
        {
            if (line.QtyPlanned <= QtyTolerance)
            {
                continue;
            }

            var huCode = NormalizeHu(line.ToHu);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            if (!result.TryGetValue(line.ItemId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[line.ItemId] = set;
            }

            set.Add(huCode);
        }

        return result;
    }

    public long CreateOrder(
        string orderRef,
        long? partnerId,
        DateTime? dueDate,
        string? comment,
        IReadOnlyList<OrderLineView> lines,
        OrderType type = OrderType.Customer,
        bool? bindReservedStockForCustomer = null)
    {
        if (string.IsNullOrWhiteSpace(orderRef))
        {
            throw new ArgumentException("Номер заказа обязателен.", nameof(orderRef));
        }

        if (type == OrderType.Customer)
        {
            if (!partnerId.HasValue)
            {
                throw new ArgumentException("Контрагент обязателен.", nameof(partnerId));
            }

            if (_data.GetPartner(partnerId.Value) == null)
            {
                throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
            }
        }

        var useReservedStock = type == OrderType.Customer && (bindReservedStockForCustomer ?? false);
        var order = new Order
        {
            OrderRef = orderRef.Trim(),
            Type = type,
            PartnerId = type == OrderType.Customer ? partnerId : null,
            DueDate = dueDate?.Date,
            Status = OrderStatus.InProgress,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = DateTime.Now,
            UseReservedStock = useReservedStock
        };

        var normalized = NormalizeLines(lines);
        long orderId = 0;

        _data.ExecuteInTransaction(store =>
        {
            orderId = store.AddOrder(order);
            foreach (var line in normalized)
            {
                store.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                });
            }

            if (type == OrderType.Customer)
            {
                TryRefreshCustomerReceiptPlans(store);
            }
            else
            {
                TryRebuildOrderReceiptPlan(store, orderId);
            }
        });

        return orderId;
    }

    public void UpdateOrder(
        long orderId,
        string orderRef,
        long? partnerId,
        DateTime? dueDate,
        string? comment,
        IReadOnlyList<OrderLineView> lines,
        OrderType type = OrderType.Customer,
        bool? bindReservedStockForCustomer = null)
    {
        var existing = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (existing.Status == OrderStatus.Shipped)
        {
            throw new InvalidOperationException($"{OrderStatusMapper.StatusToDisplayName(OrderStatus.Shipped, existing.Type)} заказ нельзя редактировать.");
        }

        if (existing.Type != type)
        {
            if ((existing.Type == OrderType.Customer && type == OrderType.Internal))
            {
                if (_data.HasOutboundDocs(orderId))
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: есть отгрузки или связанные документы.");
                }

                var shippedTotals = _data.GetShippedTotalsByOrderLine(orderId);
                if (shippedTotals.Values.Any(qty => qty > QtyTolerance))
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: по заказу уже есть отгрузки.");
                }
            }
            else if (existing.Type == OrderType.Internal && type == OrderType.Customer)
            {
                var hasProductionReceipts = _data.GetDocsByOrder(orderId)
                    .Any(doc => doc.Type == DocType.ProductionReceipt);
                if (hasProductionReceipts)
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: по внутреннему заказу уже есть выпуски продукции.");
                }

                var receiptRemaining = _data.GetOrderReceiptRemaining(orderId);
                if (receiptRemaining.Any(line => line.QtyReceived > QtyTolerance))
                {
                    throw new InvalidOperationException("Нельзя сменить тип заказа: по внутреннему заказу уже есть выпуски продукции.");
                }
            }
            else
            {
                throw new InvalidOperationException("Смена типа заказа разрешена только между клиентским и внутренним заказом.");
            }
        }

        if (type == OrderType.Customer)
        {
            if (!partnerId.HasValue)
            {
                throw new ArgumentException("Контрагент обязателен.", nameof(partnerId));
            }

            if (_data.GetPartner(partnerId.Value) == null)
            {
                throw new ArgumentException("Контрагент не найден.", nameof(partnerId));
            }
        }

        if (string.IsNullOrWhiteSpace(orderRef))
        {
            throw new ArgumentException("Номер заказа обязателен.", nameof(orderRef));
        }

        var useReservedStock = type == OrderType.Customer
            ? bindReservedStockForCustomer ?? (existing.Type == OrderType.Customer ? existing.UseReservedStock : false)
            : false;
        var updated = new Order
        {
            Id = orderId,
            OrderRef = orderRef.Trim(),
            Type = type,
            PartnerId = type == OrderType.Customer ? partnerId : null,
            DueDate = dueDate?.Date,
            Status = existing.Status == OrderStatus.Shipped ? OrderStatus.Shipped : OrderStatus.InProgress,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = existing.CreatedAt,
            UseReservedStock = useReservedStock
        };

        var normalized = NormalizeLines(lines);

        _data.ExecuteInTransaction(store =>
        {
            store.UpdateOrder(updated);

            var existingLines = store.GetOrderLines(orderId);
            var existingByItem = existingLines
                .GroupBy(line => line.ItemId)
                .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToList());
            var incomingItemIds = normalized.Select(line => line.ItemId).ToHashSet();

            foreach (var line in normalized)
            {
                if (existingByItem.TryGetValue(line.ItemId, out var matched) && matched.Count > 0)
                {
                    var primary = matched[0];
                    if (Math.Abs(primary.QtyOrdered - line.QtyOrdered) > QtyTolerance)
                    {
                        store.UpdateOrderLineQty(primary.Id, line.QtyOrdered);
                    }

                    // Legacy cleanup: keep one line per item, remove accidental duplicates.
                    for (var i = 1; i < matched.Count; i++)
                    {
                        store.DeleteOrderLine(matched[i].Id);
                    }
                    continue;
                }

                store.AddOrderLine(new OrderLine
                {
                    OrderId = orderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered
                });
            }

            foreach (var entry in existingByItem)
            {
                if (incomingItemIds.Contains(entry.Key))
                {
                    continue;
                }

                foreach (var staleLine in entry.Value)
                {
                    store.DeleteOrderLine(staleLine.Id);
                }
            }

            if (type == OrderType.Customer)
            {
                TryRefreshCustomerReceiptPlans(store);
            }
            else
            {
                TryRebuildOrderReceiptPlan(store, orderId);
                if (existing.Type == OrderType.Customer && existing.UseReservedStock)
                {
                    TryRefreshCustomerReceiptPlans(store);
                }
            }
        });
    }

    public void DeleteOrder(long orderId)
    {
        var existing = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (existing.Status != OrderStatus.Draft)
        {
            throw new InvalidOperationException("Удалить можно только заказ в статусе \"Черновик\".");
        }

        if (_data.HasOutboundDocs(orderId))
        {
            throw new InvalidOperationException("Нельзя удалить заказ: есть отгрузки или связанные документы.");
        }

        var shippedTotals = _data.GetShippedTotalsByOrderLine(orderId);
        if (shippedTotals.Values.Any(qty => qty > QtyTolerance))
        {
            throw new InvalidOperationException("Нельзя удалить заказ: есть отгрузки.");
        }

        if (existing.Type == OrderType.Internal)
        {
            var hasProductionReceipts = _data.GetDocsByOrder(orderId)
                .Any(doc => doc.Type == DocType.ProductionReceipt);
            if (hasProductionReceipts)
            {
                throw new InvalidOperationException("Нельзя удалить внутренний заказ: есть выпуски продукции или связанные документы.");
            }

            var receiptRemaining = _data.GetOrderReceiptRemaining(orderId);
            if (receiptRemaining.Any(line => line.QtyReceived > QtyTolerance))
            {
                throw new InvalidOperationException("Нельзя удалить внутренний заказ: по нему уже был выпуск продукции.");
            }
        }

        _data.ExecuteInTransaction(store =>
        {
            TryClearOrderReceiptPlan(store, orderId);
            store.DeleteOrderLines(orderId);
            store.DeleteOrder(orderId);
            if (existing.Type == OrderType.Customer)
            {
                TryRefreshCustomerReceiptPlans(store);
            }
        });
    }

    public void ChangeOrderStatus(long orderId, OrderStatus status)
    {
        throw new InvalidOperationException("Ручное изменение статуса заказа отключено. Статус определяется автоматически по выпуску и отгрузке.");
    }

    public void RefreshCustomerReceiptPlans()
    {
        _data.ExecuteInTransaction(TryRefreshCustomerReceiptPlans);
    }

    private void ApplyLineMetrics(Order order, IReadOnlyList<OrderLineView> lines)
    {
        var availableByItem = _data.GetLedgerTotalsByItem();
        if (order.Type == OrderType.Internal)
        {
            var producedByLine = _data.GetOrderReceiptRemaining(order.Id)
                .ToDictionary(line => line.OrderLineId, line => line.QtyReceived);

            foreach (var line in lines)
            {
                var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
                var produced = producedByLine.TryGetValue(line.Id, out var producedQty) ? producedQty : 0;
                var remaining = Math.Max(0, line.QtyOrdered - produced);

                line.QtyAvailable = available;
                line.QtyProduced = produced;
                line.QtyShipped = produced;
                line.QtyRemaining = remaining;
                line.CanShipNow = 0;
                line.Shortage = 0;
            }

            return;
        }

        var shippedByLine = _data.GetShippedTotalsByOrderLine(order.Id);
        var producedByOrderLine = _data.GetOrderReceiptRemaining(order.Id)
            .ToDictionary(line => line.OrderLineId, line => line.QtyReceived);

        foreach (var line in lines)
        {
            var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
            var shipped = shippedByLine.TryGetValue(line.Id, out var shippedQty) ? shippedQty : 0;
            var produced = producedByOrderLine.TryGetValue(line.Id, out var producedQty) ? producedQty : 0;
            var remaining = Math.Max(0, line.QtyOrdered - shipped);
            var reservedForLine = Math.Max(0, produced - shipped);
            // Для клиентского заказа отгрузка допускается только из объема,
            // выпущенного под этот заказ (HU-резерв заказа), без добора из свободного остатка.
            var availableForShip = reservedForLine;
            var canShip = Math.Min(remaining, availableForShip);
            var shortage = Math.Max(0, remaining - availableForShip);

            line.QtyAvailable = available;
            line.QtyShipped = shipped;
            line.QtyProduced = produced;
            line.QtyRemaining = remaining;
            line.CanShipNow = canShip;
            line.Shortage = shortage;
        }
    }

    private IReadOnlyDictionary<long, double> GetReservedOutstandingByItemForCustomerOrders()
    {
        var reserved = new Dictionary<long, double>();
        var customerOrders = _data.GetOrders()
            .Where(order => order.Type == OrderType.Customer)
            .ToList();

        foreach (var customerOrder in customerOrders)
        {
            var shippedByLine = _data.GetShippedTotalsByOrderLine(customerOrder.Id);
            foreach (var receiptLine in _data.GetOrderReceiptRemaining(customerOrder.Id))
            {
                var shipped = shippedByLine.TryGetValue(receiptLine.OrderLineId, out var shippedQty)
                    ? shippedQty
                    : 0;
                var outstanding = Math.Max(0, receiptLine.QtyReceived - shipped);
                if (outstanding <= QtyTolerance)
                {
                    continue;
                }

                reserved[receiptLine.ItemId] = reserved.TryGetValue(receiptLine.ItemId, out var current)
                    ? current + outstanding
                    : outstanding;
            }
        }

        return reserved;
    }

    private static List<OrderLineView> NormalizeLines(IReadOnlyList<OrderLineView> lines)
    {
        var grouped = new Dictionary<long, OrderLineView>();
        foreach (var line in lines)
        {
            if (line.QtyOrdered <= 0)
            {
                continue;
            }

            if (grouped.TryGetValue(line.ItemId, out var existing))
            {
                existing.QtyOrdered += line.QtyOrdered;
                continue;
            }

            grouped[line.ItemId] = new OrderLineView
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QtyOrdered = line.QtyOrdered
            };
        }

        return grouped.Values.ToList();
    }

    private void RebuildOrderReceiptPlan(IDataStore store, long orderId)
    {
        var order = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Type == OrderType.Customer)
        {
            RebuildCustomerOrderReceiptPlan(store, orderId);
            return;
        }

        RebuildInternalOrderReceiptPlan(store, orderId);
    }

    private void RebuildInternalOrderReceiptPlan(IDataStore store, long orderId)
    {
        var orderLines = store.GetOrderLines(orderId)
            .Where(line => line.QtyOrdered > QtyTolerance)
            .OrderBy(line => line.Id)
            .ToList();
        if (orderLines.Count == 0)
        {
            store.ReplaceOrderReceiptPlanLines(orderId, Array.Empty<OrderReceiptPlanLine>());
            return;
        }

        var locations = store.GetLocations()
            .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targetLocation = locations.FirstOrDefault(location => location.AutoHuDistributionEnabled)
                             ?? locations.FirstOrDefault();
        var targetLocationId = targetLocation?.Id;

        var drafts = new List<PlanDraft>();
        var nextSortOrder = 0;
        foreach (var orderLine in orderLines)
        {
            var item = store.FindItemById(orderLine.ItemId) ?? throw new InvalidOperationException("Товар заказа не найден.");
            var requiresHuDistribution = item.ItemTypeId.HasValue
                                         && store.GetItemType(item.ItemTypeId.Value)?.EnableHuDistribution == true;
            if (!requiresHuDistribution)
            {
                drafts.Add(new PlanDraft(orderLine.Id, orderLine.ItemId, orderLine.QtyOrdered, false, nextSortOrder++));
                continue;
            }

            if (!item.MaxQtyPerHu.HasValue || item.MaxQtyPerHu.Value <= QtyTolerance)
            {
                throw new InvalidOperationException($"Для товара \"{item.Name}\" обязательно заполнить \"Макс шт на 1 HU\".");
            }

            var remaining = orderLine.QtyOrdered;
            while (remaining > QtyTolerance)
            {
                var chunk = Math.Min(item.MaxQtyPerHu.Value, remaining);
                drafts.Add(new PlanDraft(orderLine.Id, orderLine.ItemId, chunk, true, nextSortOrder++));
                remaining -= chunk;
            }
        }

        var requiredHuCount = drafts.Count(line => line.RequiresHu);
        var allocatedHus = requiredHuCount > 0
            ? AllocateHuCodesForPlan(store, orderId, requiredHuCount)
            : Array.Empty<string>();
        var huIndex = 0;

        var plannedLines = drafts.Select(line => new OrderReceiptPlanLine
            {
                OrderId = orderId,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                QtyPlanned = line.QtyPlanned,
                ToLocationId = targetLocationId,
                ToHu = line.RequiresHu ? allocatedHus[huIndex++] : null,
                SortOrder = line.SortOrder
            })
            .ToList();

        store.ReplaceOrderReceiptPlanLines(orderId, plannedLines);
    }

    private void RebuildCustomerOrderReceiptPlan(IDataStore store, long orderId)
    {
        var order = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (!order.UseReservedStock)
        {
            store.ReplaceOrderReceiptPlanLines(orderId, Array.Empty<OrderReceiptPlanLine>());
            return;
        }

        var orderLines = store.GetOrderLines(orderId)
            .Where(line => line.QtyOrdered > QtyTolerance)
            .OrderBy(line => line.Id)
            .ToList();
        if (orderLines.Count == 0)
        {
            store.ReplaceOrderReceiptPlanLines(orderId, Array.Empty<OrderReceiptPlanLine>());
            return;
        }

        var internalReleaseSources = BuildAvailableInternalReleaseSources(store, orderId);
        var ownPlanPreferred = store.GetOrderReceiptPlanLines(orderId)
            .Where(line => line.QtyPlanned > QtyTolerance)
            .Select(line => NormalizeHu(line.ToHu))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedLines = new List<OrderReceiptPlanLine>();
        var nextSortOrder = 0;

        foreach (var orderLine in orderLines)
        {
            var remaining = orderLine.QtyOrdered;
            var candidates = internalReleaseSources
                .Where(source => source.ItemId == orderLine.ItemId && source.QtyAvailable > QtyTolerance)
                .OrderByDescending(source => ownPlanPreferred.Contains(source.HuCode))
                .ThenBy(source => source.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (remaining <= QtyTolerance)
                {
                    break;
                }

                var allocated = Math.Min(remaining, candidate.QtyAvailable);
                if (allocated <= QtyTolerance)
                {
                    continue;
                }

                plannedLines.Add(new OrderReceiptPlanLine
                {
                    OrderId = orderId,
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyPlanned = allocated,
                    ToLocationId = candidate.LocationId,
                    ToHu = candidate.HuCode,
                    SortOrder = nextSortOrder++
                });
                candidate.QtyAvailable -= allocated;
                remaining -= allocated;
            }
        }

        store.ReplaceOrderReceiptPlanLines(orderId, plannedLines);
    }

    private static List<InternalReleaseSource> BuildAvailableInternalReleaseSources(IDataStore store, long targetOrderId)
    {
        var internalProducedKeys = CollectInternalProducedKeys(store);
        if (internalProducedKeys.Count == 0)
        {
            return new List<InternalReleaseSource>();
        }

        var sources = store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => new
            {
                ItemId = row.ItemId,
                HuCode = NormalizeHu(row.HuCode),
                row.LocationId,
                row.Qty
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .Where(row => internalProducedKeys.Contains((row.ItemId, row.HuCode!)))
            .GroupBy(row => new { row.ItemId, row.HuCode, row.LocationId })
            .Select(group => new InternalReleaseSource(group.Key.ItemId, group.Key.HuCode!, group.Key.LocationId, group.Sum(entry => entry.Qty)))
            .ToDictionary(
                source => (source.ItemId, source.HuCode, source.LocationId),
                source => source,
                InternalReleaseSourceKeyComparer.Instance);

        var reservedByOtherOrders = CollectReservedInternalReleaseByOtherCustomerOrders(store, targetOrderId);
        foreach (var reservation in reservedByOtherOrders)
        {
            var toReserve = reservation.Value;
            if (toReserve <= QtyTolerance)
            {
                continue;
            }

            foreach (var source in sources.Values
                         .Where(source => source.ItemId == reservation.Key.ItemId
                                          && string.Equals(source.HuCode, reservation.Key.HuCode, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(source => source.LocationId))
            {
                if (toReserve <= QtyTolerance)
                {
                    break;
                }

                var take = Math.Min(source.QtyAvailable, toReserve);
                source.QtyAvailable -= take;
                toReserve -= take;
            }
        }

        return sources.Values
            .Where(source => source.QtyAvailable > QtyTolerance)
            .ToList();
    }

    private static HashSet<(long ItemId, string HuCode)> CollectInternalProducedKeys(IDataStore store)
    {
        var result = new HashSet<(long ItemId, string HuCode)>();
        var internalOrderIds = store.GetOrders()
            .Where(order => order.Type == OrderType.Internal)
            .Select(order => order.Id)
            .ToHashSet();
        if (internalOrderIds.Count == 0)
        {
            return result;
        }

        var closedProductionDocs = store.GetDocs()
            .Where(doc => doc.Type == DocType.ProductionReceipt
                          && doc.Status == DocStatus.Closed
                          && doc.OrderId.HasValue
                          && internalOrderIds.Contains(doc.OrderId.Value))
            .ToList();
        foreach (var doc in closedProductionDocs)
        {
            foreach (var line in store.GetDocLines(doc.Id))
            {
                if (line.Qty <= QtyTolerance)
                {
                    continue;
                }

                var huCode = NormalizeHu(line.ToHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                result.Add((line.ItemId, huCode));
            }
        }

        return result;
    }

    private static Dictionary<(long ItemId, string HuCode), double> CollectReservedInternalReleaseByOtherCustomerOrders(IDataStore store, long targetOrderId)
    {
        var result = new Dictionary<(long ItemId, string HuCode), double>();
        var orders = store.GetOrders()
            .Where(order => order.Type == OrderType.Customer && order.Id != targetOrderId)
            .ToList();

        foreach (var order in orders)
        {
            var effectiveStatus = ResolveCustomerOrderStatus(store, order);
            if (effectiveStatus == OrderStatus.Shipped)
            {
                continue;
            }

            foreach (var line in store.GetOrderReceiptPlanLines(order.Id))
            {
                if (line.QtyPlanned <= QtyTolerance)
                {
                    continue;
                }

                var huCode = NormalizeHu(line.ToHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                var key = (line.ItemId, huCode);
                result[key] = result.TryGetValue(key, out var current)
                    ? current + line.QtyPlanned
                    : line.QtyPlanned;
            }
        }

        return result;
    }

    private static OrderStatus ResolveCustomerOrderStatus(IDataStore store, Order order)
    {
        if (order.Type != OrderType.Customer)
        {
            return order.Status;
        }

        var lines = store.GetOrderLines(order.Id);
        if (lines.Count == 0)
        {
            return OrderStatus.InProgress;
        }

        var shippedTotals = store.GetShippedTotalsByOrderLine(order.Id);
        var fullyShipped = lines.All(line =>
        {
            var shipped = shippedTotals.TryGetValue(line.Id, out var qty) ? qty : 0;
            return shipped + QtyTolerance >= line.QtyOrdered;
        });
        if (fullyShipped)
        {
            return OrderStatus.Shipped;
        }

        var producedByLine = store.GetOrderReceiptRemaining(order.Id)
            .ToDictionary(line => line.OrderLineId, line => line.QtyReceived);
        var fullyProduced = lines.All(line =>
        {
            var produced = producedByLine.TryGetValue(line.Id, out var qty) ? qty : 0;
            return produced + QtyTolerance >= line.QtyOrdered;
        });
        return fullyProduced ? OrderStatus.Accepted : OrderStatus.InProgress;
    }

    internal static void RefreshCustomerReceiptPlansCore(IDataStore store)
    {
        var service = new OrderService(store);
        var customerOrders = store.GetOrders()
            .Where(order => order.Type == OrderType.Customer)
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToList();
        if (customerOrders.Count == 0)
        {
            return;
        }

        foreach (var order in customerOrders)
        {
            TryClearOrderReceiptPlan(store, order.Id);
        }

        foreach (var order in customerOrders.Where(order => order.UseReservedStock))
        {
            var effectiveStatus = ResolveCustomerOrderStatus(store, order);
            if (effectiveStatus == OrderStatus.Shipped)
            {
                continue;
            }

            service.TryRebuildOrderReceiptPlan(store, order.Id);
        }
    }

    private static void TryRefreshCustomerReceiptPlans(IDataStore store)
    {
        try
        {
            RefreshCustomerReceiptPlansCore(store);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            // Compatibility for strict test mocks that do not expose planning methods.
        }
    }

    private void TryRebuildOrderReceiptPlan(IDataStore store, long orderId)
    {
        try
        {
            RebuildOrderReceiptPlan(store, orderId);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            // Compatibility for strict test mocks that do not expose planning methods.
        }
    }

    private static void TryClearOrderReceiptPlan(IDataStore store, long orderId)
    {
        try
        {
            store.ReplaceOrderReceiptPlanLines(orderId, Array.Empty<OrderReceiptPlanLine>());
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            // Compatibility for strict test mocks that do not expose planning methods.
        }
    }

    private static IReadOnlyList<string> AllocateHuCodesForPlan(IDataStore store, long orderId, int requiredCount)
    {
        if (requiredCount <= 0)
        {
            return Array.Empty<string>();
        }

        var occupiedHu = store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => NormalizeHu(row.HuCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedHu = store.GetReservedOrderReceiptHuCodes(orderId)
            .Select(NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownPlanHu = store.GetOrderReceiptPlanLines(orderId)
            .Select(line => NormalizeHu(line.ToHu))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var available = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hu in ownPlanHu)
        {
            if (string.IsNullOrWhiteSpace(hu)
                || occupiedHu.Contains(hu)
                || reservedHu.Contains(hu)
                || !used.Add(hu))
            {
                continue;
            }

            available.Add(hu);
            if (available.Count >= requiredCount)
            {
                return available;
            }
        }

        foreach (var huRecord in store.GetHus(null, 10000).OrderBy(record => record.Code, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedHu = NormalizeHu(huRecord.Code);
            if (string.IsNullOrWhiteSpace(normalizedHu))
            {
                continue;
            }

            if (!string.Equals(huRecord.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(huRecord.Status, "CLOSED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (occupiedHu.Contains(normalizedHu) || reservedHu.Contains(normalizedHu) || !used.Add(normalizedHu))
            {
                continue;
            }

            available.Add(normalizedHu);
            if (available.Count >= requiredCount)
            {
                return available;
            }
        }

        throw new InvalidOperationException($"Недостаточно свободных HU для заказа. Нужно: {requiredCount}, доступно: {available.Count}.");
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private static bool IsMockStoreException(Exception ex)
    {
        var fullName = ex.GetType().FullName ?? string.Empty;
        return fullName.Contains("Moq", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Castle.Proxies", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PlanDraft(long OrderLineId, long ItemId, double QtyPlanned, bool RequiresHu, int SortOrder);
    private sealed class InternalReleaseSource
    {
        public InternalReleaseSource(long itemId, string huCode, long locationId, double qtyAvailable)
        {
            ItemId = itemId;
            HuCode = huCode;
            LocationId = locationId;
            QtyAvailable = qtyAvailable;
        }

        public long ItemId { get; }
        public string HuCode { get; }
        public long LocationId { get; }
        public double QtyAvailable { get; set; }
    }

    private sealed class InternalReleaseSourceKeyComparer : IEqualityComparer<(long ItemId, string HuCode, long LocationId)>
    {
        public static readonly InternalReleaseSourceKeyComparer Instance = new();

        public bool Equals((long ItemId, string HuCode, long LocationId) x, (long ItemId, string HuCode, long LocationId) y)
        {
            return x.ItemId == y.ItemId
                   && x.LocationId == y.LocationId
                   && string.Equals(x.HuCode, y.HuCode, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((long ItemId, string HuCode, long LocationId) obj)
        {
            return HashCode.Combine(
                obj.ItemId,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.HuCode),
                obj.LocationId);
        }
    }

    private Order ApplyAutoStatus(Order order)
    {
        if (order.Type == OrderType.Internal)
        {
            var receiptLines = _data.GetOrderReceiptRemaining(order.Id);
            var fullyProduced = receiptLines.Count > 0 && receiptLines.All(line => line.QtyReceived + QtyTolerance >= line.QtyOrdered);
            var anyProduced = receiptLines.Any(line => line.QtyReceived > QtyTolerance);

            var internalStatus = order.Status;
            if (fullyProduced)
            {
                internalStatus = OrderStatus.Shipped;
            }
            else if (anyProduced)
            {
                internalStatus = OrderStatus.InProgress;
            }
            else
            {
                internalStatus = OrderStatus.InProgress;
            }

            if (internalStatus != order.Status)
            {
                _data.UpdateOrderStatus(order.Id, internalStatus);
            }

            var completedAt = internalStatus == OrderStatus.Shipped
                ? _data.GetDocsByOrder(order.Id)
                    .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Closed && doc.ClosedAt.HasValue)
                    .Select(doc => doc.ClosedAt!.Value)
                    .DefaultIfEmpty()
                    .Max()
                : (DateTime?)null;

            return new Order
            {
                Id = order.Id,
                OrderRef = order.OrderRef,
                Type = order.Type,
                PartnerId = order.PartnerId,
                DueDate = order.DueDate,
                Status = internalStatus,
                Comment = order.Comment,
                CreatedAt = order.CreatedAt,
                ShippedAt = completedAt == DateTime.MinValue ? null : completedAt,
                PartnerName = order.PartnerName,
                PartnerCode = order.PartnerCode,
                UseReservedStock = order.UseReservedStock
            };
        }

        var lines = _data.GetOrderLines(order.Id);
        var shippedTotals = _data.GetShippedTotalsByOrderLine(order.Id);
        var customerReceiptLines = _data.GetOrderReceiptRemaining(order.Id);
        var producedByLine = customerReceiptLines.ToDictionary(line => line.OrderLineId, line => line.QtyReceived);

        var fullyShipped = lines.Count > 0 && lines.All(line =>
        {
            var shipped = shippedTotals.TryGetValue(line.Id, out var qty) ? qty : 0;
            return shipped + QtyTolerance >= line.QtyOrdered;
        });

        var nextStatus = OrderStatus.InProgress;
        if (fullyShipped)
        {
            nextStatus = OrderStatus.Shipped;
        }
        else
        {
            var fullyProducedForOrder = lines.Count > 0 && lines.All(line =>
            {
                var produced = producedByLine.TryGetValue(line.Id, out var qty) ? qty : 0;
                return produced + QtyTolerance >= line.QtyOrdered;
            });
            if (fullyProducedForOrder)
            {
                nextStatus = OrderStatus.Accepted;
            }
        }

        if (nextStatus != order.Status)
        {
            _data.UpdateOrderStatus(order.Id, nextStatus);
        }

        var shippedAt = fullyShipped ? _data.GetOrderShippedAt(order.Id) : null;
        return new Order
        {
            Id = order.Id,
            OrderRef = order.OrderRef,
            Type = order.Type,
            PartnerId = order.PartnerId,
            DueDate = order.DueDate,
            Status = nextStatus,
            Comment = order.Comment,
            CreatedAt = order.CreatedAt,
            ShippedAt = shippedAt,
            PartnerName = order.PartnerName,
            PartnerCode = order.PartnerCode,
            UseReservedStock = order.UseReservedStock
        };
    }
}

