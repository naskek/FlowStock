using System.Diagnostics;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderLineHuDetailsBuilder
{
    private const double QtyTolerance = 0.000001d;

    public static IReadOnlyDictionary<long, OrderLineHuDetails> BuildByOrder(
        IDataStore store,
        Order order,
        IReadOnlyList<OrderLineView> lineViews,
        OrderLineHuDetailsTiming? timing = null,
        OrderLineHuFateTiming? fateTiming = null)
    {
        var totalStopwatch = timing != null ? Stopwatch.StartNew() : null;
        var phaseStopwatch = timing != null ? Stopwatch.StartNew() : null;
        var orderLines = store.GetOrderLines(order.Id);
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.GetOrderLinesMs = elapsed);

        phaseStopwatch?.Restart();
        var warehouseRows = BuildWarehouseRows(store, order);
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.BuildWarehouseRowsMs = elapsed);

        phaseStopwatch?.Restart();
        var productionResult = BuildProductionRows(store, order.Id);
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.BuildProductionRowsMs = elapsed);

        IReadOnlyDictionary<long, IReadOnlyList<OrderLineProductionHuRow>> productionRowsWithFate = productionResult.Rows;
        if (productionResult.FateSources.Count == 0)
        {
            if (timing != null)
            {
                timing.HuFateMs = 0;
            }

            MarkFateSkipped(fateTiming);
        }
        else
        {
            phaseStopwatch?.Restart();
            var fateRows = ScopedOrderLineHuFateDisplayBuilder.Build(
                store,
                order,
                productionResult.FateSources,
                fateTiming);
            RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.HuFateMs = elapsed);
            productionRowsWithFate = AttachProductionFate(productionResult.Rows, fateRows);
        }

        phaseStopwatch?.Restart();
        var shippedRows = BuildShippedRows(store, order.Id);
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.BuildShippedRowsMs = elapsed);

        phaseStopwatch?.Restart();
        var confirmedProductionByLine = OrderReceiptRemainingCalculator
            .BuildConfirmedReceiptLedgerTotalsByOrderLine(store, order.Id, orderLines);
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.ConfirmedReceiptLedgerTotalsMs = elapsed);

        phaseStopwatch?.Restart();
        var customerCoverageByLine = order.Type == OrderType.Customer
            ? CustomerProtectedCoverageCalculator.BuildByOrderLine(store, order.Id, orderLines)
            : null;
        if (order.Type == OrderType.Customer)
        {
            RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.CustomerCoverageMs = elapsed);
        }

        phaseStopwatch?.Restart();
        var result = lineViews.ToDictionary(
            line => line.Id,
            line =>
            {
                var lineWarehouseRows = warehouseRows.GetValueOrDefault(line.Id)
                    ?? Array.Empty<OrderLineWarehouseHuRow>();
                var lineProductionRows = productionRowsWithFate.GetValueOrDefault(line.Id)
                    ?? Array.Empty<OrderLineProductionHuRow>();
                var lineShippedRows = shippedRows.GetValueOrDefault(line.Id)
                    ?? Array.Empty<OrderLineShippedHuRow>();
                var warehouseBoundQty = lineWarehouseRows.Sum(row => Math.Max(0, row.Qty));
                var productionFilledQty = confirmedProductionByLine.GetValueOrDefault(line.Id);
                var shippedQty = order.Type == OrderType.Customer
                    ? Math.Max(0, line.QtyShipped)
                    : 0d;
                double coveredQty;

                if (order.Type == OrderType.Customer)
                {
                    coveredQty = customerCoverageByLine?.GetValueOrDefault(line.Id)?.DeduplicatedQty ?? 0d;
                }
                else
                {
                    coveredQty = Math.Max(0, line.QtyProduced);
                    productionFilledQty = coveredQty;
                }

                return new OrderLineHuDetails
                {
                    WarehouseHuRows = lineWarehouseRows,
                    ProductionHuRows = lineProductionRows,
                    ShippedHuRows = lineShippedRows,
                    Coverage = new OrderLineCoverage
                    {
                        OrderedQty = Math.Max(0, line.QtyOrdered),
                        WarehouseBoundQty = Math.Max(0, warehouseBoundQty),
                        ProductionFilledQty = Math.Max(0, productionFilledQty),
                        ShippedQty = shippedQty,
                        CoveredQty = Math.Max(0, coveredQty),
                        MissingQty = Math.Max(0, line.QtyOrdered - coveredQty)
                    }
                };
            });
        RecordPhase(timing, phaseStopwatch, static (value, elapsed) => value.FinalMappingMs = elapsed);
        totalStopwatch?.Stop();
        if (timing != null && totalStopwatch != null)
        {
            timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<OrderLineWarehouseHuRow>> BuildWarehouseRows(
        IDataStore store,
        Order order)
    {
        if (order.Type != OrderType.Customer)
        {
            return new Dictionary<long, IReadOnlyList<OrderLineWarehouseHuRow>>();
        }

        var locations = store.GetLocations().ToDictionary(location => location.Id);
        return CustomerOutboundBoundHuService.GetUnshippedBoundHuLines(store, order.Id)
            .GroupBy(row => row.OrderLineId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OrderLineWarehouseHuRow>)group
                    .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                    .Select(row =>
                    {
                        var location = row.FromLocationId.HasValue
                            ? locations.GetValueOrDefault(row.FromLocationId.Value)
                            : null;
                        return new OrderLineWarehouseHuRow
                        {
                            HuCode = row.HuCode,
                            Qty = row.Qty,
                            LocationCode = row.FromLocationCode ?? location?.Code,
                            LocationName = location?.Name,
                            IsBoundToOrder = true
                        };
                    })
                    .ToArray());
    }

    private static ProductionRowsResult BuildProductionRows(
        IDataStore store,
        long orderId)
    {
        var rows = new Dictionary<long, List<OrderLineProductionHuRow>>();
        var fateSources = new List<ScopedOrderLineHuFateSource>();
        foreach (var doc in store.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => ProductionOrderLineHuCodes.IsActivePalletStatus(pallet.Status))
                         .Where(pallet => !string.IsNullOrWhiteSpace(pallet.HuCode)))
            {
                var components = pallet.Lines.Count > 0
                    ? pallet.Lines
                    : [new ProductionPalletComponentLine
                    {
                        OrderLineId = pallet.OrderLineId,
                        ItemId = pallet.ItemId,
                        PlannedQty = pallet.PlannedQty,
                        FilledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                            ? pallet.PlannedQty
                            : 0
                    }];
                foreach (var component in components.Where(component => component.OrderLineId.HasValue))
                {
                    var filledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                        ? component.FilledQty > QtyTolerance ? component.FilledQty : component.PlannedQty
                        : Math.Max(0, component.FilledQty);
                    Add(rows, component.OrderLineId!.Value, new OrderLineProductionHuRow
                    {
                        HuCode = pallet.HuCode.Trim(),
                        PalletStatus = pallet.EffectiveStatus,
                        PlannedQty = Math.Max(0, component.PlannedQty),
                        FilledQty = Math.Max(0, filledQty),
                        PrdRef = doc.DocRef
                    });
                    if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    {
                        fateSources.Add(new ScopedOrderLineHuFateSource
                        {
                            OrderLineId = component.OrderLineId.Value,
                            ItemId = component.ItemId,
                            HuCode = pallet.HuCode.Trim(),
                            Qty = Math.Max(0, filledQty)
                        });
                    }
                }
            }
        }

        return new ProductionRowsResult(
            rows.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<OrderLineProductionHuRow>)pair.Value
                    .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.PrdRef, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            fateSources);
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<OrderLineProductionHuRow>> AttachProductionFate(
        IReadOnlyDictionary<long, IReadOnlyList<OrderLineProductionHuRow>> productionRows,
        IReadOnlyDictionary<long, OrderLineHuDisplayEntry[]> fateRows)
    {
        var fateByLineHu = fateRows
            .SelectMany(pair => pair.Value.Select(entry => new
            {
                OrderLineId = pair.Key,
                HuCode = NormalizeHu(entry.HuCode),
                Entry = entry
            }))
            .Where(row => row.HuCode != null)
            .ToDictionary(
                row => (row.OrderLineId, row.HuCode!),
                row => row.Entry);

        return productionRows.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<OrderLineProductionHuRow>)pair.Value
                .Select(row =>
                {
                    var normalizedHu = NormalizeHu(row.HuCode);
                    var fate = normalizedHu == null
                        ? null
                        : fateByLineHu.GetValueOrDefault((pair.Key, normalizedHu));
                    return new OrderLineProductionHuRow
                    {
                        HuCode = row.HuCode,
                        PalletStatus = row.PalletStatus,
                        PlannedQty = row.PlannedQty,
                        FilledQty = row.FilledQty,
                        PrdRef = row.PrdRef,
                        FateCode = fate?.FateCode,
                        FateLabel = fate?.FateLabel,
                        FateOrderRef = fate?.FateOrderRef,
                        FateDocRef = fate?.FateDocRef,
                        FateQty = fate?.FateQty
                    };
                })
                .ToArray());
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<OrderLineShippedHuRow>> BuildShippedRows(
        IDataStore store,
        long orderId)
    {
        var rows = store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Closed)
            .SelectMany(doc => store.GetDocLines(doc.Id))
            .Where(line => line.OrderLineId.HasValue
                           && line.Qty > QtyTolerance
                           && !string.IsNullOrWhiteSpace(line.FromHu))
            .GroupBy(line => (OrderLineId: line.OrderLineId!.Value, HuCode: NormalizeHu(line.FromHu)!))
            .Select(group => new
            {
                group.Key.OrderLineId,
                Row = new OrderLineShippedHuRow
                {
                    HuCode = group.Key.HuCode,
                    Qty = group.Sum(line => line.Qty)
                }
            });

        return rows.GroupBy(row => row.OrderLineId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OrderLineShippedHuRow>)group
                    .Select(row => row.Row)
                    .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
    }

    private static void Add(
        IDictionary<long, List<OrderLineProductionHuRow>> rows,
        long orderLineId,
        OrderLineProductionHuRow row)
    {
        if (!rows.TryGetValue(orderLineId, out var lineRows))
        {
            lineRows = new List<OrderLineProductionHuRow>();
            rows[orderLineId] = lineRows;
        }

        lineRows.Add(row);
    }

    private static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private static void MarkFateSkipped(OrderLineHuFateTiming? timing)
    {
        if (timing == null)
        {
            return;
        }

        timing.Skipped = true;
        timing.Scoped = true;
        timing.ScopedLookupMs = 0;
        timing.ScopedKeysCount = 0;
        timing.GetOrdersMs = 0;
        timing.OrdersCount = 0;
        timing.GetDocsMs = 0;
        timing.DocsCount = 0;
        timing.GetHuStockRowsMs = 0;
        timing.HuStockRowsCount = 0;
        timing.BuildSourcesMs = 0;
        timing.SourcesCount = 0;
        timing.BuildReservationsMs = 0;
        timing.ReservationsCount = 0;
        timing.BuildShipmentsMs = 0;
        timing.ShipmentsCount = 0;
        timing.FinalRowsMs = 0;
        timing.FinalRowsCount = 0;
        timing.TotalMs = 0;
    }

    private sealed record ProductionRowsResult(
        IReadOnlyDictionary<long, IReadOnlyList<OrderLineProductionHuRow>> Rows,
        IReadOnlyList<ScopedOrderLineHuFateSource> FateSources);

    private static void RecordPhase(
        OrderLineHuDetailsTiming? timing,
        Stopwatch? stopwatch,
        Action<OrderLineHuDetailsTiming, long> assign)
    {
        stopwatch?.Stop();
        if (timing != null && stopwatch != null)
        {
            assign(timing, stopwatch.ElapsedMilliseconds);
        }
    }
}
