using System.Diagnostics;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class ScopedOrderLineHuFateDisplayBuilder
{
    public const string StockCandidateKind = "STOCK";
    public const string ReservationCandidateKind = "RESERVATION";
    public const string ShipmentCandidateKind = "SHIPMENT";

    public static Dictionary<long, OrderLineHuDisplayEntry[]> Build(
        IDataStore store,
        Order sourceOrder,
        IReadOnlyCollection<ScopedOrderLineHuFateSource> sources,
        OrderLineHuFateTiming? timing = null)
    {
        var normalizedSources = sources
            .Select(source => new
            {
                Source = source,
                HuCode = NormalizeHu(source.HuCode)
            })
            .Where(row => row.Source.OrderLineId > 0
                          && row.Source.ItemId > 0
                          && row.HuCode != null)
            .Select(row => new ScopedOrderLineHuFateSource
            {
                OrderLineId = row.Source.OrderLineId,
                ItemId = row.Source.ItemId,
                HuCode = row.HuCode!,
                Qty = row.Source.Qty
            })
            .ToArray();
        var keys = normalizedSources
            .Select(source => new ScopedOrderLineHuFateKey(source.ItemId, source.HuCode))
            .Distinct()
            .ToArray();

        InitializeTiming(timing, keys.Length, normalizedSources.Length);
        if (keys.Length == 0 || store is not IOptimizedOrderLineHuFateStore optimizedStore)
        {
            MarkSkipped(timing);
            return new Dictionary<long, OrderLineHuDisplayEntry[]>();
        }

        var totalStopwatch = timing != null ? Stopwatch.StartNew() : null;
        var phaseStopwatch = timing != null ? Stopwatch.StartNew() : null;
        var candidates = optimizedStore.GetScopedOrderLineHuFateCandidates(keys);
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.ScopedLookupMs = elapsed);

        var normalizedCandidates = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                HuCode = NormalizeHu(candidate.HuCode)
            })
            .Where(row => row.Candidate.ItemId > 0 && row.HuCode != null)
            .Select(row => new ScopedOrderLineHuFateCandidate
            {
                Kind = row.Candidate.Kind,
                ItemId = row.Candidate.ItemId,
                HuCode = row.HuCode!,
                Qty = row.Candidate.Qty,
                TargetOrderId = row.Candidate.TargetOrderId,
                TargetOrderLineId = row.Candidate.TargetOrderLineId,
                TargetOrderRef = row.Candidate.TargetOrderRef,
                DocId = row.Candidate.DocId,
                DocRef = row.Candidate.DocRef,
                ClosedAt = row.Candidate.ClosedAt,
                CreatedAt = row.Candidate.CreatedAt
            })
            .ToArray();

        var stockByKey = normalizedCandidates
            .Where(candidate => string.Equals(candidate.Kind, StockCandidateKind, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => new ScopedOrderLineHuFateKey(candidate.ItemId, candidate.HuCode))
            .ToDictionary(group => group.Key, group => group.Sum(candidate => candidate.Qty));
        if (timing != null)
        {
            timing.HuStockRowsCount = stockByKey.Count;
        }

        phaseStopwatch?.Restart();
        var reservationByKey = normalizedCandidates
            .Where(candidate => string.Equals(candidate.Kind, ReservationCandidateKind, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => stockByKey.GetValueOrDefault(
                new ScopedOrderLineHuFateKey(candidate.ItemId, candidate.HuCode)) > StockQuantityRules.QtyTolerance)
            .GroupBy(candidate => new ScopedOrderLineHuFateKey(candidate.ItemId, candidate.HuCode))
            .ToDictionary(group => group.Key, group => group
                .OrderBy(candidate => candidate.TargetOrderId)
                .ThenBy(candidate => candidate.TargetOrderLineId)
                .First());
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.BuildReservationsMs = elapsed);
        if (timing != null)
        {
            timing.ReservationsCount = reservationByKey.Count;
        }

        phaseStopwatch?.Restart();
        var latestShipmentByKey = normalizedCandidates
            .Where(candidate => string.Equals(candidate.Kind, ShipmentCandidateKind, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => new ScopedOrderLineHuFateKey(candidate.ItemId, candidate.HuCode))
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(candidate => candidate.ClosedAt ?? DateTime.MinValue)
                .ThenByDescending(candidate => candidate.CreatedAt ?? DateTime.MinValue)
                .ThenByDescending(candidate => candidate.DocId ?? 0)
                .First());
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.BuildShipmentsMs = elapsed);
        if (timing != null)
        {
            timing.ShipmentsCount = latestShipmentByKey.Count;
            timing.DocsCount = latestShipmentByKey.Values
                .Where(candidate => candidate.DocId.HasValue)
                .Select(candidate => candidate.DocId!.Value)
                .Distinct()
                .Count();
            timing.OrdersCount = normalizedCandidates
                .Where(candidate => candidate.TargetOrderId.HasValue)
                .Select(candidate => candidate.TargetOrderId!.Value)
                .Distinct()
                .Count();
        }

        phaseStopwatch?.Restart();
        var rows = new Dictionary<long, Dictionary<string, OrderLineHuDisplayEntry>>();
        foreach (var source in normalizedSources)
        {
            var key = new ScopedOrderLineHuFateKey(source.ItemId, source.HuCode);
            if (latestShipmentByKey.TryGetValue(key, out var shipment) && shipment.TargetOrderId.HasValue)
            {
                var sameOrder = shipment.TargetOrderId.Value == sourceOrder.Id;
                var targetOrderRef = OrderRef(shipment.TargetOrderRef, shipment.TargetOrderId.Value);
                var fateLabel = sameOrder ? "отгружено" : $"→ отгружено заказ {targetOrderRef}";
                Add(rows, source.OrderLineId, new OrderLineHuDisplayEntry(
                    source.HuCode,
                    sameOrder ? "отгружено" : "наполнено",
                    sameOrder ? shipment.Qty : source.Qty,
                    IsWarehouseBound: false,
                    SortOrder: OrderLineHuFateDisplayBuilder.ShippedSortOrder,
                    sameOrder ? null : fateLabel,
                    FateCode: OrderLineHuFateDisplayBuilder.ShippedFateCode,
                    FateLabel: fateLabel,
                    FateOrderRef: targetOrderRef,
                    FateDocRef: shipment.DocRef,
                    FateQty: shipment.Qty));
            }
            else if (reservationByKey.TryGetValue(key, out var reservation) && reservation.TargetOrderId.HasValue)
            {
                var sameOrder = reservation.TargetOrderId.Value == sourceOrder.Id;
                var targetOrderRef = OrderRef(reservation.TargetOrderRef, reservation.TargetOrderId.Value);
                var fateLabel = sameOrder ? "резерв этого заказа" : $"→ резерв заказ {targetOrderRef}";
                Add(rows, source.OrderLineId, new OrderLineHuDisplayEntry(
                    source.HuCode,
                    "наполнено",
                    source.Qty,
                    IsWarehouseBound: false,
                    SortOrder: sameOrder
                        ? OrderLineHuFateDisplayBuilder.FilledSortOrder
                        : OrderLineHuFateDisplayBuilder.ReservedSortOrder,
                    sameOrder ? null : fateLabel,
                    FateCode: OrderLineHuFateDisplayBuilder.ReservedFateCode,
                    FateLabel: fateLabel,
                    FateOrderRef: targetOrderRef,
                    FateQty: reservation.Qty));
            }
            else if (stockByKey.GetValueOrDefault(key) > StockQuantityRules.QtyTolerance)
            {
                Add(rows, source.OrderLineId, new OrderLineHuDisplayEntry(
                    source.HuCode,
                    "наполнено",
                    source.Qty,
                    IsWarehouseBound: false,
                    SortOrder: OrderLineHuFateDisplayBuilder.FilledSortOrder,
                    FateCode: OrderLineHuFateDisplayBuilder.OnStockFateCode,
                    FateLabel: "на складе",
                    FateQty: stockByKey[key]));
            }
        }

        var result = rows.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Values
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.HuCode, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.FinalRowsMs = elapsed);
        totalStopwatch?.Stop();
        if (timing != null && totalStopwatch != null)
        {
            timing.FinalRowsCount = result.Values.Sum(entries => entries.Length);
            timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    private static void InitializeTiming(OrderLineHuFateTiming? timing, int keyCount, int sourceCount)
    {
        if (timing == null)
        {
            return;
        }

        timing.Skipped = false;
        timing.Scoped = true;
        timing.ScopedKeysCount = keyCount;
        timing.GetOrdersMs = 0;
        timing.GetDocsMs = 0;
        timing.GetHuStockRowsMs = 0;
        timing.BuildSourcesMs = 0;
        timing.SourcesCount = sourceCount;
    }

    private static void MarkSkipped(OrderLineHuFateTiming? timing)
    {
        if (timing == null)
        {
            return;
        }

        timing.Skipped = true;
        timing.ScopedLookupMs = 0;
        timing.OrdersCount = 0;
        timing.DocsCount = 0;
        timing.HuStockRowsCount = 0;
        timing.BuildReservationsMs = 0;
        timing.ReservationsCount = 0;
        timing.BuildShipmentsMs = 0;
        timing.ShipmentsCount = 0;
        timing.FinalRowsMs = 0;
        timing.FinalRowsCount = 0;
        timing.TotalMs = 0;
    }

    private static void Add(
        Dictionary<long, Dictionary<string, OrderLineHuDisplayEntry>> rows,
        long orderLineId,
        OrderLineHuDisplayEntry candidate)
    {
        if (!rows.TryGetValue(orderLineId, out var byHu))
        {
            byHu = new Dictionary<string, OrderLineHuDisplayEntry>(StringComparer.OrdinalIgnoreCase);
            rows[orderLineId] = byHu;
        }

        byHu[candidate.HuCode] = candidate;
    }

    private static string OrderRef(string? orderRef, long orderId) =>
        string.IsNullOrWhiteSpace(orderRef) ? orderId.ToString() : orderRef.Trim();

    private static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private static void RecordPhase(
        OrderLineHuFateTiming? timing,
        Stopwatch? stopwatch,
        Action<OrderLineHuFateTiming, long> assign)
    {
        stopwatch?.Stop();
        if (timing != null && stopwatch != null)
        {
            assign(timing, stopwatch.ElapsedMilliseconds);
        }
    }
}
