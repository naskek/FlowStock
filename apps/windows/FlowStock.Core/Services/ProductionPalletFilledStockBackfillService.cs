using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletFilledStockBackfillService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;
    private readonly DocumentService _documents = new(dataStore);

    public IReadOnlyList<FilledProductionPalletStockAnalysis> GetStockAnalyses()
    {
        return _dataStore.GetFilledProductionPalletStockMetrics()
            .Select(ProductionPalletStockBackfillDecision.Analyze)
            .ToList();
    }

    public IReadOnlyList<FilledProductionPalletStockAnalysis> GetFilledWithoutStock()
    {
        return GetStockAnalyses()
            .Where(analysis => analysis.Decision == ProductionPalletStockBackfillDecisionCodes.SafeToBackfill
                               && analysis.MissingQty > StockQuantityRules.QtyTolerance)
            .ToList();
    }

    public IReadOnlyList<FilledStockReverseCandidate> GetReverseCandidates()
    {
        return GetStockAnalyses()
            .Where(ProductionPalletStockBackfillDecision.IsReverseCandidate)
            .Select(ProductionPalletStockBackfillDecision.ToReverseCandidate)
            .ToList();
    }

    public ProductionPalletFilledStockBackfillResult BackfillFilledStock(bool dryRun)
    {
        var analyses = GetStockAnalyses();
        var toApply = analyses
            .Where(analysis => analysis.Decision == ProductionPalletStockBackfillDecisionCodes.SafeToBackfill
                               && analysis.MissingQty > StockQuantityRules.QtyTolerance)
            .ToList();

        if (dryRun)
        {
            return ProductionPalletFilledStockBackfillResult.ForDryRun(analyses, toApply);
        }

        if (toApply.Count == 0)
        {
            return ProductionPalletFilledStockBackfillResult.ForApply(analyses, Array.Empty<FilledProductionPalletStockAnalysis>(), 0);
        }

        var applied = new List<FilledProductionPalletStockAnalysis>();
        var ledgerRowsWritten = 0;
        _dataStore.ExecuteInTransaction(store =>
        {
            foreach (var analysis in toApply)
            {
                if (!analysis.ToLocationId.HasValue || string.IsNullOrWhiteSpace(analysis.HuCode))
                {
                    continue;
                }

                var currentLedgerQty = store.GetLedgerBalance(analysis.ItemId, analysis.ToLocationId.Value, analysis.HuCode);
                var missingQty = Math.Max(0, analysis.PlannedQty - currentLedgerQty);
                if (missingQty <= StockQuantityRules.QtyTolerance)
                {
                    continue;
                }

                store.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = DateTime.Now,
                    DocId = analysis.PrdDocId,
                    ItemId = analysis.ItemId,
                    LocationId = analysis.ToLocationId.Value,
                    QtyDelta = missingQty,
                    HuCode = analysis.HuCode.Trim()
                });
                applied.Add(analysis);
                ledgerRowsWritten++;
            }
        });

        return ProductionPalletFilledStockBackfillResult.ForApply(analyses, applied, ledgerRowsWritten);
    }

    public ReverseFilledStockBackfillDraftResult CreateReverseBackfillDraft(
        IReadOnlyCollection<long> palletIds,
        string? comment)
    {
        if (palletIds == null || palletIds.Count == 0)
        {
            return ReverseFilledStockBackfillDraftResult.Fail("INVALID_PALLET_IDS", "Укажите pallet_ids для сторно.");
        }

        var requestedIds = palletIds.Where(id => id > 0).Distinct().ToHashSet();
        var candidatesByKey = GetReverseCandidates()
            .Where(candidate => requestedIds.Contains(candidate.PalletId))
            .GroupBy(candidate => (candidate.PalletId, candidate.ItemId))
            .ToDictionary(group => group.Key, group => group.First());

        var warnings = new List<string>();
        foreach (var palletId in requestedIds)
        {
            if (candidatesByKey.Keys.All(key => key.PalletId != palletId))
            {
                warnings.Add($"Паллета {palletId} не является reverse candidate и пропущена.");
            }
        }

        var lines = candidatesByKey.Values
            .Where(candidate => candidate.ReverseQty > StockQuantityRules.QtyTolerance)
            .OrderBy(candidate => candidate.PrdDocId)
            .ThenBy(candidate => candidate.HuCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ItemId)
            .ToList();

        if (lines.Count == 0)
        {
            return ReverseFilledStockBackfillDraftResult.Fail(
                "NO_REVERSE_LINES",
                "Нет строк для сторно ошибочного backfill.",
                warnings);
        }

        var userComment = string.IsNullOrWhiteSpace(comment)
            ? "Сторно ошибочного backfill для уже отгруженных FILLED pallets"
            : comment.Trim();
        var docComment = BuildReverseComment(userComment, lines);
        var now = DateTime.Now;
        var docRef = _documents.GenerateDocRef(DocType.InventoryCorrection, now);
        var docId = _documents.CreateDoc(
            DocType.InventoryCorrection,
            docRef,
            docComment,
            partnerId: null,
            orderRef: null,
            shippingRef: null,
            orderId: null,
            hydrateOrderLines: false);

        var lineCount = 0;
        foreach (var candidate in lines)
        {
            if (!candidate.LocationId.HasValue)
            {
                warnings.Add($"Паллета {candidate.PalletId}, item {candidate.ItemId}: не указана локация.");
                continue;
            }

            _documents.AddDocLine(
                docId,
                candidate.ItemId,
                -candidate.ReverseQty,
                fromLocationId: null,
                toLocationId: candidate.LocationId.Value,
                fromHu: null,
                toHu: candidate.HuCode.Trim());
            lineCount++;
        }

        if (lineCount == 0)
        {
            return ReverseFilledStockBackfillDraftResult.Fail(
                "NO_REVERSE_LINES",
                "Не удалось сформировать строки сторно.",
                warnings);
        }

        return ReverseFilledStockBackfillDraftResult.Ok(
            docId,
            docRef,
            lineCount,
            "Создан черновик сторно ошибочного backfill. Проведите документ через стандартное закрытие.",
            warnings);
    }

    private static string BuildReverseComment(string userComment, IReadOnlyList<FilledStockReverseCandidate> lines)
    {
        var summary = string.Join(
            ", ",
            lines.Select(line => $"{line.HuCode}/item{line.ItemId}=-{line.ReverseQty:0.###}"));
        return string.Join(' ', new[]
        {
            "Reverse erroneous FILLED pallet backfill.",
            userComment,
            $"Lines: {summary}."
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}

public sealed class ProductionPalletFilledStockBackfillResult
{
    public bool DryRun { get; init; }
    public IReadOnlyList<FilledProductionPalletStockAnalysis> Analyses { get; init; } = Array.Empty<FilledProductionPalletStockAnalysis>();
    public IReadOnlyList<FilledProductionPalletStockAnalysis> Applied { get; init; } = Array.Empty<FilledProductionPalletStockAnalysis>();
    public int LedgerRowsWritten { get; init; }

    [Obsolete("Use Analyses")]
    public IReadOnlyList<FilledProductionPalletStockGap> Gaps =>
        Analyses.Select(FilledProductionPalletStockGap.FromAnalysis).ToList();

    public static ProductionPalletFilledStockBackfillResult ForDryRun(
        IReadOnlyList<FilledProductionPalletStockAnalysis> analyses,
        IReadOnlyList<FilledProductionPalletStockAnalysis> wouldApply) =>
        new()
        {
            DryRun = true,
            Analyses = analyses,
            Applied = wouldApply
        };

    public static ProductionPalletFilledStockBackfillResult ForApply(
        IReadOnlyList<FilledProductionPalletStockAnalysis> analyses,
        IReadOnlyList<FilledProductionPalletStockAnalysis> applied,
        int ledgerRowsWritten) =>
        new()
        {
            DryRun = false,
            Analyses = analyses,
            Applied = applied,
            LedgerRowsWritten = ledgerRowsWritten
        };
}
