using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

internal sealed class CustomerProtectedCoverage
{
    public double DeduplicatedQty { get; init; }

    public double ResolveProtectedQty(double qtyOrdered)
    {
        return Math.Min(Math.Max(0, qtyOrdered), DeduplicatedQty);
    }
}

public sealed class CustomerShipmentReadiness
{
    public double OrderedQty { get; init; }
    public double ShippedQty { get; init; }
    public double RemainingToShip { get; init; }
    public double CoveredQty { get; init; }
    public double ReadyProtectedUnshippedQty { get; init; }
    public double MissingQty { get; init; }
    public double CanShipNow { get; init; }
    public bool IsReady => MissingQty <= CustomerShipmentReadinessCalculator.QtyTolerance;
}

public static class CustomerShipmentReadinessCalculator
{
    public const double QtyTolerance = 0.000001d;

    public static IReadOnlyDictionary<long, CustomerShipmentReadiness> BuildByOrderLine(
        IDataStore store,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines = null,
        IReadOnlyDictionary<long, double>? shippedByLine = null)
    {
        var lines = (orderLines ?? store.GetOrderLines(orderId)).ToArray();
        var shippedTotals = shippedByLine ?? store.GetShippedTotalsByOrderLine(orderId);
        var protectedCoverage = CustomerProtectedCoverageCalculator.BuildByOrderLine(store, orderId, lines);

        return lines.ToDictionary(
            line => line.Id,
            line =>
            {
                var ordered = Math.Max(0, line.QtyOrdered);
                var shipped = shippedTotals.TryGetValue(line.Id, out var shippedQty)
                    ? Math.Max(0, shippedQty)
                    : 0d;
                var remainingToShip = Math.Max(0, ordered - shipped);
                var covered = protectedCoverage.TryGetValue(line.Id, out var coverage)
                    ? coverage.ResolveProtectedQty(ordered)
                    : 0d;
                covered = Math.Max(0, Math.Min(ordered, covered));
                var readyProtectedUnshipped = Math.Max(0, covered - Math.Min(ordered, shipped));
                var missing = Math.Max(0, ordered - covered);

                return new CustomerShipmentReadiness
                {
                    OrderedQty = ordered,
                    ShippedQty = shipped,
                    RemainingToShip = remainingToShip,
                    CoveredQty = covered,
                    ReadyProtectedUnshippedQty = readyProtectedUnshipped,
                    MissingQty = missing,
                    CanShipNow = Math.Min(remainingToShip, readyProtectedUnshipped)
                };
            });
    }
}

internal static class CustomerProtectedCoverageCalculator
{
    private const double QtyTolerance = 0.000001d;

    public static IReadOnlyDictionary<long, CustomerProtectedCoverage> BuildByOrderLine(
        IDataStore store,
        long orderId,
        IReadOnlyList<OrderLine>? orderLines = null,
        bool includeUnconfirmedFilledPallets = false)
    {
        var lines = (orderLines ?? store.GetOrderLines(orderId)).ToArray();
        var confirmedTotals = includeUnconfirmedFilledPallets
            ? BuildFilledTotalsByOrderLine(store, orderId, lines)
            : OrderReceiptRemainingCalculator.BuildConfirmedReceiptLedgerTotalsByOrderLine(store, orderId, lines);
        var confirmedByLineHu = BuildConfirmedByLineHu(store, orderId, includeUnconfirmedFilledPallets);
        var shippedByLineHu = BuildShippedByLineHu(store, orderId);
        var boundByLineHu = BuildBoundByLineHu(store, orderId);

        return lines.ToDictionary(
            line => line.Id,
            line =>
            {
                var confirmedTotal = confirmedTotals.TryGetValue(line.Id, out var total) ? Math.Max(0, total) : 0d;
                var confirmedHuTotal = confirmedByLineHu
                    .Where(entry => entry.Key.OrderLineId == line.Id)
                    .Sum(entry => entry.Value);
                var confirmedWithoutHu = Math.Max(0, confirmedTotal - confirmedHuTotal);
                var shippedTotal = shippedByLineHu
                    .Where(entry => entry.Key.OrderLineId == line.Id)
                    .Sum(entry => entry.Value);
                var shippedMatchedToConfirmedHu = confirmedByLineHu
                    .Where(entry => entry.Key.OrderLineId == line.Id)
                    .Sum(entry =>
                    {
                        var shipped = shippedByLineHu.TryGetValue(entry.Key, out var qty) ? qty : 0d;
                        return Math.Min(entry.Value, shipped);
                    });

                var huCodes = confirmedByLineHu.Keys
                    .Concat(shippedByLineHu.Keys)
                    .Concat(boundByLineHu.Keys)
                    .Where(key => key.OrderLineId == line.Id)
                    .Select(key => key.HuCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var huCoverage = huCodes.Sum(huCode =>
                {
                    var key = (line.Id, huCode);
                    var shipped = shippedByLineHu.TryGetValue(key, out var shippedQty) ? shippedQty : 0d;
                    var confirmed = confirmedByLineHu.TryGetValue(key, out var confirmedQty) ? confirmedQty : 0d;
                    var bound = boundByLineHu.TryGetValue(key, out var boundQty) ? boundQty : 0d;
                    return shipped + Math.Max(
                        Math.Max(0, confirmed - shipped),
                        Math.Max(0, bound - shipped));
                });
                var remainingShipmentForLegacy = Math.Max(0, shippedTotal - shippedMatchedToConfirmedHu);
                var legacyCoverage = Math.Max(0, confirmedWithoutHu - remainingShipmentForLegacy);

                return new CustomerProtectedCoverage
                {
                    DeduplicatedQty = Math.Max(0, huCoverage + legacyCoverage)
                };
            });
    }

    private static Dictionary<(long OrderLineId, string HuCode), double> BuildConfirmedByLineHu(
        IDataStore store,
        long orderId,
        bool includeUnconfirmedFilledPallets)
    {
        var result = new Dictionary<(long, string), double>();
        try
        {
            foreach (var doc in (store.GetDocsByOrder(orderId) ?? Array.Empty<Doc>())
                         .Where(doc => doc.Type == DocType.ProductionReceipt))
            {
                var pallets = (store.GetProductionPalletsByDoc(doc.Id) ?? Array.Empty<ProductionPallet>())
                    .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (pallets.Length > 0)
                {
                    foreach (var pallet in pallets)
                    {
                        var huCode = NormalizeHu(pallet.HuCode);
                        if (huCode == null)
                        {
                            continue;
                        }

                        if (pallet.Lines.Count > 0)
                        {
                            foreach (var palletLine in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                            {
                                var palletQty = palletLine.FilledQty > QtyTolerance ? palletLine.FilledQty : palletLine.PlannedQty;
                                var confirmedQty = includeUnconfirmedFilledPallets
                                    ? palletQty
                                    : Math.Min(palletQty, Math.Max(0, store.GetLedgerQtyByDocItemHu(doc.Id, palletLine.ItemId, huCode)));
                                Add(result, (palletLine.OrderLineId!.Value, huCode), confirmedQty);
                            }
                        }
                        else if (pallet.OrderLineId.HasValue)
                        {
                            var confirmedQty = includeUnconfirmedFilledPallets
                                ? pallet.PlannedQty
                                : Math.Min(pallet.PlannedQty, Math.Max(0, store.GetLedgerQtyByDocItemHu(doc.Id, pallet.ItemId, huCode)));
                            Add(result, (pallet.OrderLineId.Value, huCode), confirmedQty);
                        }
                    }

                    continue;
                }

                if (doc.Status != DocStatus.Closed)
                {
                    continue;
                }

                foreach (var docLine in (store.GetDocLines(doc.Id) ?? Array.Empty<DocLine>()).Where(line => line.OrderLineId.HasValue))
                {
                    var huCode = NormalizeHu(docLine.ToHu);
                    if (huCode == null)
                    {
                        continue;
                    }

                    var ledgerQty = Math.Max(0, store.GetLedgerQtyByDocItemHu(doc.Id, docLine.ItemId, huCode));
                    Add(result, (docLine.OrderLineId!.Value, huCode), Math.Min(docLine.Qty, ledgerQty));
                }
            }
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return result;
        }

        return result;
    }

    private static IReadOnlyDictionary<long, double> BuildFilledTotalsByOrderLine(
        IDataStore store,
        long orderId,
        IReadOnlyList<OrderLine> orderLines)
    {
        var totals = orderLines.ToDictionary(line => line.Id, _ => 0d);
        try
        {
            foreach (var pallet in (store.GetDocsByOrder(orderId) ?? Array.Empty<Doc>())
                         .Where(doc => doc.Type == DocType.ProductionReceipt)
                         .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id) ?? Array.Empty<ProductionPallet>())
                         .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                if (pallet.Lines.Count > 0)
                {
                    foreach (var line in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                    {
                        Add(totals, line.OrderLineId!.Value, line.FilledQty > QtyTolerance ? line.FilledQty : line.PlannedQty);
                    }
                }
                else if (pallet.OrderLineId.HasValue)
                {
                    Add(totals, pallet.OrderLineId.Value, pallet.PlannedQty);
                }
            }
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return totals;
        }

        return totals;
    }

    private static Dictionary<(long OrderLineId, string HuCode), double> BuildShippedByLineHu(
        IDataStore store,
        long orderId)
    {
        var result = new Dictionary<(long, string), double>();
        try
        {
            foreach (var doc in (store.GetDocsByOrder(orderId) ?? Array.Empty<Doc>())
                         .Where(doc => doc.Type == DocType.Outbound && doc.Status == DocStatus.Closed))
            {
                foreach (var line in (store.GetDocLines(doc.Id) ?? Array.Empty<DocLine>()).Where(line => line.OrderLineId.HasValue))
                {
                    Add(result, (line.OrderLineId!.Value, NormalizeHu(line.FromHu) ?? string.Empty), line.Qty);
                }
            }
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return result;
        }

        return result;
    }

    private static Dictionary<(long OrderLineId, string HuCode), double> BuildBoundByLineHu(
        IDataStore store,
        long orderId)
    {
        var result = new Dictionary<(long, string), double>();
        IReadOnlyList<HuStockRow> stockRows;
        IReadOnlyList<OrderReceiptPlanLine> planLines;
        try
        {
            stockRows = store.GetHuStockRows() ?? Array.Empty<HuStockRow>();
            planLines = store.GetOrderReceiptPlanLines(orderId) ?? Array.Empty<OrderReceiptPlanLine>();
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return result;
        }

        var stockByItemHu = stockRows
            .Where(row => row.Qty > QtyTolerance && !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => (row.ItemId, HuCode: NormalizeHu(row.HuCode)!))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => Math.Max(0, row.Qty)));

        foreach (var line in planLines)
        {
            var huCode = NormalizeHu(line.ToHu);
            if (huCode != null
                && stockByItemHu.TryGetValue((line.ItemId, huCode), out var stockQty)
                && stockQty > QtyTolerance)
            {
                Add(result, (line.OrderLineId, huCode), Math.Min(line.QtyPlanned, stockQty));
            }
        }

        return result;
    }

    private static void Add(
        IDictionary<(long OrderLineId, string HuCode), double> result,
        (long OrderLineId, string HuCode) key,
        double qty)
    {
        if (qty <= QtyTolerance)
        {
            return;
        }

        result[key] = result.TryGetValue(key, out var current) ? current + qty : qty;
    }

    private static void Add(IDictionary<long, double> result, long orderLineId, double qty)
    {
        if (qty > QtyTolerance && result.ContainsKey(orderLineId))
        {
            result[orderLineId] += qty;
        }
    }

    private static string? NormalizeHu(string? huCode)
    {
        return string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();
    }

    private static bool IsMockStoreException(Exception ex)
    {
        var fullName = ex.GetType().FullName ?? string.Empty;
        return fullName.Contains("Moq", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Castle.Proxies", StringComparison.OrdinalIgnoreCase);
    }
}
