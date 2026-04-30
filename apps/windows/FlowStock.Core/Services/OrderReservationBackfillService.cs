using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using System.Globalization;

namespace FlowStock.Core.Services;

public sealed class OrderReservationBackfillService
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _data;

    public OrderReservationBackfillService(IDataStore data)
    {
        _data = data;
    }

    public OrderReservationBackfillReport Run(OrderReservationBackfillOptions? options = null)
    {
        var apply = options?.Apply == true;
        if (!apply)
        {
            return BuildAndMaybeApply(_data, apply: false);
        }

        OrderReservationBackfillReport? result = null;
        _data.ExecuteInTransaction(store =>
        {
            result = BuildAndMaybeApply(store, apply: true);
        });

        return result ?? throw new InvalidOperationException("Backfill transaction did not produce a report.");
    }

    private static OrderReservationBackfillReport BuildAndMaybeApply(IDataStore store, bool apply)
    {
        var customerOrders = store.GetOrders()
            .Where(order => order.Type == OrderType.Customer)
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToList();

        var existingByOrder = customerOrders.ToDictionary(
            order => order.Id,
            order => store.GetOrderReceiptPlanLines(order.Id).ToList());
        var conflicts = DetectCurrentConflicts(store, customerOrders);
        var conflictKeys = conflicts
            .Select(conflict => (conflict.ItemId, HuCode: NormalizeHu(conflict.HuCode)!))
            .ToHashSet();

        var sources = BuildAvailableReservationSources(store, conflictKeys);
        var desiredByOrder = new Dictionary<long, List<OrderReceiptPlanLine>>();
        var orderReports = new List<OrderReservationBackfillOrderReport>();
        var activeCount = 0;
        var inactiveCount = 0;

        foreach (var order in customerOrders)
        {
            var existing = existingByOrder[order.Id];
            var effectiveStatus = ResolveCustomerOrderStatus(store, order);
            var active = order.UseReservedStock
                         && effectiveStatus is not OrderStatus.Shipped and not OrderStatus.Cancelled;
            List<OrderReceiptPlanLine> desired;
            IReadOnlyList<OrderReservationBackfillLineReport> lineReports;
            string? skipReason = null;

            if (!active)
            {
                inactiveCount++;
                desired = new List<OrderReceiptPlanLine>();
                skipReason = order.UseReservedStock
                    ? $"inactive order: status={OrderStatusMapper.StatusToString(effectiveStatus)}"
                    : "inactive order: bind_reserved_stock=false";
                lineReports = BuildInactiveLineReports(store, order, skipReason);
            }
            else
            {
                activeCount++;
                var result = BuildPlanForOrder(store, order, existing, sources, conflictKeys);
                desired = result.PlanLines;
                lineReports = result.LineReports;
            }

            desiredByOrder[order.Id] = desired;
            orderReports.Add(new OrderReservationBackfillOrderReport(
                order.Id,
                order.OrderRef,
                OrderStatusMapper.StatusToString(effectiveStatus),
                active,
                existing.Count,
                desired.Count,
                existing.Sum(line => line.QtyPlanned),
                desired.Sum(line => line.QtyPlanned),
                !PlanSignaturesEqual(existing, desired),
                skipReason,
                lineReports));
        }

        if (apply)
        {
            foreach (var order in customerOrders)
            {
                store.ReplaceOrderReceiptPlanLines(order.Id, Array.Empty<OrderReceiptPlanLine>());
            }

            foreach (var order in customerOrders)
            {
                if (!desiredByOrder.TryGetValue(order.Id, out var desired) || desired.Count == 0)
                {
                    continue;
                }

                store.ReplaceOrderReceiptPlanLines(order.Id, desired);
            }
        }

        return new OrderReservationBackfillReport(
            apply,
            customerOrders.Count,
            activeCount,
            inactiveCount,
            existingByOrder.Values.Sum(lines => lines.Count),
            desiredByOrder.Values.Sum(lines => lines.Count),
            existingByOrder.Values.Sum(lines => lines.Sum(line => line.QtyPlanned)),
            desiredByOrder.Values.Sum(lines => lines.Sum(line => line.QtyPlanned)),
            orderReports.Count(report => report.WillChange),
            conflicts,
            orderReports);
    }

    private static PlanBuildResult BuildPlanForOrder(
        IDataStore store,
        Order order,
        IReadOnlyList<OrderReceiptPlanLine> existing,
        List<ReservationSource> sources,
        IReadOnlySet<(long ItemId, string HuCode)> conflictKeys)
    {
        var shippedByLine = store.GetShippedTotalsByOrderLine(order.Id);
        var ownPlanPreferred = existing
            .Select(line => NormalizeHu(line.ToHu))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stockQtyByItem = BuildHuStockQtyByItem(store);
        var conflictItems = conflictKeys
            .Select(key => key.ItemId)
            .ToHashSet();
        var planned = new List<OrderReceiptPlanLine>();
        var lineReports = new List<OrderReservationBackfillLineReport>();
        var nextSortOrder = 0;

        var orderLines = store.GetOrderLines(order.Id)
            .Where(line => line.QtyOrdered > QtyTolerance)
            .OrderBy(line => line.Id)
            .ToList();

        foreach (var orderLine in orderLines)
        {
            var shipped = shippedByLine.TryGetValue(orderLine.Id, out var shippedQty) ? shippedQty : 0;
            var remaining = Math.Max(0, orderLine.QtyOrdered - shipped);
            if (remaining <= QtyTolerance)
            {
                lineReports.Add(new OrderReservationBackfillLineReport(
                    orderLine.Id,
                    orderLine.ItemId,
                    0,
                    0,
                    null));
                continue;
            }

            if (!ItemTypeUsesOrderReservation(store, orderLine.ItemId))
            {
                lineReports.Add(new OrderReservationBackfillLineReport(
                    orderLine.Id,
                    orderLine.ItemId,
                    remaining,
                    0,
                    "disabled item type"));
                continue;
            }

            var candidates = sources
                .Where(source => source.ItemId == orderLine.ItemId && source.QtyAvailable > QtyTolerance)
                .OrderByDescending(source => ownPlanPreferred.Contains(source.HuCode))
                .ThenBy(source => source.HuCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.LocationId)
                .ToList();
            var plannedBefore = planned.Count;
            var requested = remaining;

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

                planned.Add(new OrderReceiptPlanLine
                {
                    OrderId = order.Id,
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyPlanned = allocated,
                    ToLocationId = candidate.LocationId,
                    ToHu = candidate.HuCode,
                    SortOrder = nextSortOrder++
                });

                ExhaustHuSource(sources, candidate.ItemId, candidate.HuCode);
                remaining -= allocated;
            }

            var plannedQty = planned.Skip(plannedBefore).Sum(line => line.QtyPlanned);
            lineReports.Add(new OrderReservationBackfillLineReport(
                orderLine.Id,
                orderLine.ItemId,
                requested,
                plannedQty,
                ResolveLineSkipReason(orderLine.ItemId, requested, plannedQty, stockQtyByItem, conflictItems)));
        }

        return new PlanBuildResult(planned, lineReports);
    }

    private static void ExhaustHuSource(List<ReservationSource> sources, long itemId, string huCode)
    {
        foreach (var source in sources.Where(source => source.ItemId == itemId
                                                       && string.Equals(source.HuCode, huCode, StringComparison.OrdinalIgnoreCase)))
        {
            source.QtyAvailable = 0;
        }
    }

    private static IReadOnlyList<OrderReservationBackfillLineReport> BuildInactiveLineReports(
        IDataStore store,
        Order order,
        string skipReason)
    {
        return store.GetOrderLines(order.Id)
            .Where(line => line.QtyOrdered > QtyTolerance)
            .OrderBy(line => line.Id)
            .Select(line => new OrderReservationBackfillLineReport(
                line.Id,
                line.ItemId,
                line.QtyOrdered,
                0,
                skipReason))
            .ToList();
    }

    private static Dictionary<long, double> BuildHuStockQtyByItem(IDataStore store)
    {
        return store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => new { row.ItemId, HuCode = NormalizeHu(row.HuCode), row.Qty })
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));
    }

    private static string? ResolveLineSkipReason(
        long itemId,
        double requestedQty,
        double plannedQty,
        IReadOnlyDictionary<long, double> stockQtyByItem,
        IReadOnlySet<long> conflictItems)
    {
        if (plannedQty + QtyTolerance >= requestedQty)
        {
            return null;
        }

        if (!stockQtyByItem.TryGetValue(itemId, out var stockQty) || stockQty <= QtyTolerance)
        {
            return "no stock";
        }

        return conflictItems.Contains(itemId) ? "conflict" : "no free HU";
    }

    private static IReadOnlyList<OrderReservationBackfillConflict> DetectCurrentConflicts(
        IDataStore store,
        IReadOnlyList<Order> customerOrders)
    {
        var activeOrders = customerOrders
            .Where(order => order.UseReservedStock
                            && ResolveCustomerOrderStatus(store, order) is not OrderStatus.Shipped and not OrderStatus.Cancelled)
            .ToDictionary(order => order.Id);

        return activeOrders.Values
            .SelectMany(order => store.GetOrderReceiptPlanLines(order.Id)
                .Where(line => line.QtyPlanned > QtyTolerance)
                .Select(line => new { Order = order, Line = line, HuCode = NormalizeHu(line.ToHu) })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.HuCode)))
            .GroupBy(entry => (entry.Line.ItemId, HuCode: entry.HuCode!), new ReservationKeyComparer())
            .Select(group =>
            {
                var claims = group
                    .GroupBy(entry => entry.Order.Id)
                    .Select(orderGroup =>
                    {
                        var first = orderGroup.First().Order;
                        return new OrderReservationBackfillConflictClaim(
                            first.Id,
                            first.OrderRef,
                            orderGroup.Sum(entry => entry.Line.QtyPlanned));
                    })
                    .OrderBy(claim => claim.OrderId)
                    .ToList();

                return claims.Count > 1
                    ? new OrderReservationBackfillConflict(group.Key.HuCode, group.Key.ItemId, claims)
                    : null;
            })
            .Where(conflict => conflict != null)
            .Cast<OrderReservationBackfillConflict>()
            .OrderBy(conflict => conflict.HuCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(conflict => conflict.ItemId)
            .ToList();
    }

    private static List<ReservationSource> BuildAvailableReservationSources(
        IDataStore store,
        IReadOnlySet<(long ItemId, string HuCode)> excludedHuKeys)
    {
        return store.GetHuStockRows()
            .Where(row => row.Qty > QtyTolerance)
            .Select(row => new
            {
                row.ItemId,
                HuCode = NormalizeHu(row.HuCode),
                row.LocationId,
                row.Qty
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .Where(row => !excludedHuKeys.Contains((row.ItemId, row.HuCode!)))
            .GroupBy(row => new { row.ItemId, row.HuCode, row.LocationId })
            .Select(group => new ReservationSource(
                group.Key.ItemId,
                group.Key.HuCode!,
                group.Key.LocationId,
                group.Sum(entry => entry.Qty)))
            .Where(source => source.QtyAvailable > QtyTolerance)
            .ToList();
    }

    private static bool ItemTypeUsesOrderReservation(IDataStore store, long itemId)
    {
        var item = store.FindItemById(itemId);
        if (item?.ItemTypeId is not long itemTypeId)
        {
            return false;
        }

        return store.GetItemType(itemTypeId)?.EnableOrderReservation == true;
    }

    private static OrderStatus ResolveCustomerOrderStatus(IDataStore store, Order order)
    {
        if (order.Type != OrderType.Customer)
        {
            return order.Status;
        }

        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
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

    private static bool PlanSignaturesEqual(
        IReadOnlyList<OrderReceiptPlanLine> left,
        IReadOnlyList<OrderReceiptPlanLine> right)
    {
        return left.Select(BuildPlanSignature).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.Select(BuildPlanSignature).OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string BuildPlanSignature(OrderReceiptPlanLine line)
    {
        return string.Join(
            "|",
            line.OrderLineId.ToString(CultureInfo.InvariantCulture),
            line.ItemId.ToString(CultureInfo.InvariantCulture),
            line.QtyPlanned.ToString("0.######", CultureInfo.InvariantCulture),
            line.ToLocationId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NormalizeHu(line.ToHu) ?? string.Empty,
            line.SortOrder.ToString(CultureInfo.InvariantCulture));
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private sealed class ReservationSource
    {
        public ReservationSource(long itemId, string huCode, long locationId, double qtyAvailable)
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

    private sealed class ReservationKeyComparer : IEqualityComparer<(long ItemId, string HuCode)>
    {
        public bool Equals((long ItemId, string HuCode) x, (long ItemId, string HuCode) y)
        {
            return x.ItemId == y.ItemId && string.Equals(x.HuCode, y.HuCode, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((long ItemId, string HuCode) obj)
        {
            return HashCode.Combine(obj.ItemId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.HuCode));
        }
    }

    private sealed record PlanBuildResult(
        List<OrderReceiptPlanLine> PlanLines,
        IReadOnlyList<OrderReservationBackfillLineReport> LineReports);
}

public sealed record OrderReservationBackfillOptions(bool Apply = false);

public sealed record OrderReservationBackfillReport(
    bool Applied,
    int CustomerOrderCount,
    int ActiveCustomerOrderCount,
    int InactiveCustomerOrderCount,
    int ExistingPlanLineCount,
    int PlannedPlanLineCount,
    double ExistingPlannedQty,
    double PlannedQty,
    int ChangedOrderCount,
    IReadOnlyList<OrderReservationBackfillConflict> Conflicts,
    IReadOnlyList<OrderReservationBackfillOrderReport> Orders);

public sealed record OrderReservationBackfillOrderReport(
    long OrderId,
    string OrderRef,
    string EffectiveStatus,
    bool Active,
    int ExistingPlanLineCount,
    int PlannedPlanLineCount,
    double ExistingPlannedQty,
    double PlannedQty,
    bool WillChange,
    string? SkipReason,
    IReadOnlyList<OrderReservationBackfillLineReport> Lines);

public sealed record OrderReservationBackfillLineReport(
    long OrderLineId,
    long ItemId,
    double RequestedQty,
    double PlannedQty,
    string? SkipReason);

public sealed record OrderReservationBackfillConflict(
    string HuCode,
    long ItemId,
    IReadOnlyList<OrderReservationBackfillConflictClaim> Claims);

public sealed record OrderReservationBackfillConflictClaim(
    long OrderId,
    string OrderRef,
    double QtyPlanned);
