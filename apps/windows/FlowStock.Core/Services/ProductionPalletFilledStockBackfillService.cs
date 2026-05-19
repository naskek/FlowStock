using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletFilledStockBackfillService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<FilledProductionPalletStockGap> GetFilledWithoutStock()
    {
        return _dataStore.GetFilledProductionPalletsWithStockGaps();
    }

    public ProductionPalletFilledStockBackfillResult BackfillFilledStock(bool dryRun)
    {
        var gaps = _dataStore.GetFilledProductionPalletsWithStockGaps();
        if (dryRun)
        {
            return ProductionPalletFilledStockBackfillResult.ForDryRun(gaps);
        }

        if (gaps.Count == 0)
        {
            return ProductionPalletFilledStockBackfillResult.ForApply(gaps, Array.Empty<FilledProductionPalletStockGap>(), 0);
        }

        var applied = new List<FilledProductionPalletStockGap>();
        var ledgerRowsWritten = 0;
        _dataStore.ExecuteInTransaction(store =>
        {
            foreach (var gap in gaps)
            {
                if (!gap.ToLocationId.HasValue || string.IsNullOrWhiteSpace(gap.HuCode))
                {
                    continue;
                }

                var currentLedgerQty = store.GetLedgerBalance(gap.ItemId, gap.ToLocationId.Value, gap.HuCode);
                var missingQty = gap.PlannedQty - currentLedgerQty;
                if (StockQuantityRules.IsEffectivelyZero(missingQty) || missingQty < -StockQuantityRules.QtyTolerance)
                {
                    continue;
                }

                store.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = DateTime.Now,
                    DocId = gap.PrdDocId,
                    ItemId = gap.ItemId,
                    LocationId = gap.ToLocationId.Value,
                    QtyDelta = missingQty,
                    HuCode = gap.HuCode.Trim()
                });
                applied.Add(new FilledProductionPalletStockGap
                {
                    PalletId = gap.PalletId,
                    PrdDocId = gap.PrdDocId,
                    PrdDocRef = gap.PrdDocRef,
                    ItemId = gap.ItemId,
                    ItemName = gap.ItemName,
                    HuCode = gap.HuCode,
                    ToLocationId = gap.ToLocationId,
                    PlannedQty = gap.PlannedQty,
                    LedgerQty = currentLedgerQty,
                    MissingQty = missingQty,
                    Status = gap.Status,
                    FilledAt = gap.FilledAt
                });
                ledgerRowsWritten++;
            }
        });

        return ProductionPalletFilledStockBackfillResult.ForApply(gaps, applied, ledgerRowsWritten);
    }
}

public sealed class ProductionPalletFilledStockBackfillResult
{
    public bool DryRun { get; init; }
    public IReadOnlyList<FilledProductionPalletStockGap> Gaps { get; init; } = Array.Empty<FilledProductionPalletStockGap>();
    public IReadOnlyList<FilledProductionPalletStockGap> Applied { get; init; } = Array.Empty<FilledProductionPalletStockGap>();
    public int LedgerRowsWritten { get; init; }

    public static ProductionPalletFilledStockBackfillResult ForDryRun(IReadOnlyList<FilledProductionPalletStockGap> gaps) =>
        new()
        {
            DryRun = true,
            Gaps = gaps
        };

    public static ProductionPalletFilledStockBackfillResult ForApply(
        IReadOnlyList<FilledProductionPalletStockGap> gaps,
        IReadOnlyList<FilledProductionPalletStockGap> applied,
        int ledgerRowsWritten) =>
        new()
        {
            DryRun = false,
            Gaps = gaps,
            Applied = applied,
            LedgerRowsWritten = ledgerRowsWritten
        };
}
