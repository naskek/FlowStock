using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class HuReservationCandidatesService
{
    private readonly IDataStore _dataStore;

    public HuReservationCandidatesService(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public HuReservationCandidatesResult Build(HuReservationCandidatesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Lines.Count == 0)
        {
            return new HuReservationCandidatesResult();
        }

        var itemIds = query.Lines
            .Select(line => line.ItemId)
            .Where(itemId => itemId > 0)
            .Distinct()
            .ToArray();
        var sources = itemIds.Length == 0 || _dataStore is not IOptimizedHuReservationCandidatesStore optimizedStore
            ? Array.Empty<HuReservationCandidateSourceRow>()
            : optimizedStore.GetHuReservationCandidateSources(
                query.OrderId > 0 ? query.OrderId : null,
                itemIds,
                query.ExcludeHuCodes);

        var sourcesByItem = sources
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var autoSelectedHuKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineResults = new List<HuReservationCandidatesLineResult>(query.Lines.Count);
        foreach (var line in query.Lines)
        {
            var candidates = BuildLineCandidates(line, sourcesByItem);
            var autoSelectedQty = ApplyAutoSelection(line, candidates, autoSelectedHuKeys);
            lineResults.Add(new HuReservationCandidatesLineResult
            {
                ClientLineKey = line.ClientLineKey,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                QtyOrdered = line.QtyOrdered,
                AvailableQty = candidates.Sum(candidate => candidate.Qty),
                AutoSelectedQty = autoSelectedQty,
                Candidates = candidates
            });
        }

        return new HuReservationCandidatesResult { Lines = lineResults };
    }

    private static List<HuReservationCandidateResult> BuildLineCandidates(
        HuReservationCandidatesLineQuery line,
        IReadOnlyDictionary<long, List<HuReservationCandidateSourceRow>> sourcesByItem)
    {
        if (line.ItemId <= 0 || !sourcesByItem.TryGetValue(line.ItemId, out var rows))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<HuReservationCandidateResult>();
        foreach (var row in rows.OrderBy(row => SourceSortKey(row.Source))
                     .ThenBy(row => row.FirstReceiptAt ?? DateTime.MaxValue)
                     .ThenBy(row => row.FirstReceiptDocId ?? long.MaxValue)
                     .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.SourceOrderRef, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.SourcePrdRef, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(row.Source, OrderHuReservationApplyService.SourceLedgerStock, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dedupeKey = string.Join(
                "|",
                row.ItemId,
                row.HuCode,
                row.Source,
                row.SourceOrderId?.ToString() ?? string.Empty,
                row.SourcePrdDocId?.ToString() ?? string.Empty);
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            candidates.Add(new HuReservationCandidateResult
            {
                HuCode = row.HuCode,
                Source = row.Source,
                SourceOrderId = row.SourceOrderId,
                SourceOrderRef = row.SourceOrderRef,
                SourcePrdDocId = row.SourcePrdDocId,
                SourcePrdRef = row.SourcePrdRef,
                FirstReceiptAt = row.FirstReceiptAt,
                FirstReceiptDocId = row.FirstReceiptDocId,
                Qty = row.Qty,
                ShipReady = row.ShipReady,
                AutoSelected = false,
                ReservedByOrderId = row.ReservedByOrderId,
                ReservedByOrderRef = row.ReservedByOrderRef,
                Note = row.Note
            });
        }

        return candidates;
    }

    private static double ApplyAutoSelection(
        HuReservationCandidatesLineQuery line,
        List<HuReservationCandidateResult> candidates,
        HashSet<string> autoSelectedHuKeys)
    {
        if (line.QtyOrdered <= StockQuantityRules.QtyTolerance)
        {
            return 0;
        }

        var remaining = line.QtyOrdered;
        var autoSelectedQty = 0d;
        foreach (var candidate in ChooseAutoSelectionCandidates(line.QtyOrdered, candidates))
        {
            if (remaining <= StockQuantityRules.QtyTolerance)
            {
                break;
            }

            if (autoSelectedHuKeys.Contains(candidate.HuCode))
            {
                continue;
            }

            var allocated = Math.Min(remaining, candidate.Qty);
            if (allocated <= StockQuantityRules.QtyTolerance)
            {
                continue;
            }

            candidate.AutoSelected = true;
            autoSelectedHuKeys.Add(candidate.HuCode);
            autoSelectedQty += allocated;
            remaining -= allocated;
        }

        return Math.Min(autoSelectedQty, line.QtyOrdered);
    }

    private static IReadOnlyList<HuReservationCandidateResult> ChooseAutoSelectionCandidates(
        double qtyOrdered,
        IReadOnlyList<HuReservationCandidateResult> candidates)
    {
        var available = candidates
            .Where(candidate => candidate.Qty > StockQuantityRules.QtyTolerance)
            .ToArray();
        if (available.Length == 0 || qtyOrdered <= StockQuantityRules.QtyTolerance)
        {
            return [];
        }

        var single = available
            .Where(candidate => candidate.Qty + StockQuantityRules.QtyTolerance >= qtyOrdered)
            .OrderBy(candidate => Math.Abs(candidate.Qty - qtyOrdered))
            .ThenBy(candidate => candidate.Qty)
            .ThenBy(candidate => candidate.FirstReceiptAt ?? DateTime.MaxValue)
            .ThenBy(candidate => candidate.FirstReceiptDocId ?? long.MaxValue)
            .ThenBy(candidate => candidate.HuCode, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (single != null)
        {
            return [single];
        }

        return available
            .OrderBy(candidate => SourceSortKey(candidate.Source))
            .ThenBy(candidate => candidate.FirstReceiptAt ?? DateTime.MaxValue)
            .ThenBy(candidate => candidate.FirstReceiptDocId ?? long.MaxValue)
            .ThenBy(candidate => candidate.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int SourceSortKey(string source)
    {
        return string.Equals(source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
