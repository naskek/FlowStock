using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class HuBalanceCorrectionService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;
    private readonly DocumentService _documents = new(dataStore);

    public HuBalanceCorrectionDraftResult CreateCorrectionDraft(HuBalanceCorrectionDraftRequest request)
    {
        if (request.ItemId <= 0 || request.LocationId <= 0)
        {
            return HuBalanceCorrectionDraftResult.Fail("INVALID_KEYS", "Укажите товар и место хранения.");
        }

        var item = _dataStore.FindItemById(request.ItemId);
        if (item == null)
        {
            return HuBalanceCorrectionDraftResult.Fail("ITEM_NOT_FOUND", "Товар не найден.");
        }

        var location = _dataStore.FindLocationById(request.LocationId);
        if (location == null)
        {
            return HuBalanceCorrectionDraftResult.Fail("LOCATION_NOT_FOUND", "Место хранения не найдено.");
        }

        var filledPallets = _dataStore.GetFilledProductionPalletsByItemAndLocation(request.ItemId, request.LocationId);
        var protectedHuCodes = filledPallets
            .Select(pallet => NormalizeHu(pallet.HuCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var protectedFilledPallets = filledPallets
            .Select(pallet =>
            {
                var doc = _dataStore.GetDoc(pallet.PrdDocId);
                return new ProtectedFilledProductionPallet
                {
                    HuCode = pallet.HuCode,
                    PrdDocId = pallet.PrdDocId,
                    PrdDocRef = doc?.DocRef ?? string.Empty,
                    Status = pallet.Status,
                    PlannedQty = pallet.PlannedQty
                };
            })
            .OrderBy(pallet => pallet.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var huRows = _dataStore.GetHuStockRows()
            .Where(row => row.ItemId == request.ItemId && row.LocationId == request.LocationId)
            .Where(row => !StockQuantityRules.IsEffectivelyZero(row.Qty))
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (huRows.Count == 0)
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "NO_HU_IMBALANCE",
                "Нет ненулевых HU-остатков по указанному товару и месту.");
        }

        var candidateBalances = huRows
            .Select(row =>
            {
                var normalizedHu = NormalizeHu(row.HuCode)!;
                return new HuBalanceCorrectionCandidateBalance
                {
                    HuCode = row.HuCode.Trim(),
                    Qty = row.Qty,
                    Protected = protectedHuCodes.Contains(normalizedHu)
                };
            })
            .ToList();

        var totalAll = huRows.Sum(row => row.Qty)
                       + _dataStore.GetLedgerBalance(request.ItemId, request.LocationId, null);
        var correctionRows = huRows
            .Where(row => !protectedHuCodes.Contains(NormalizeHu(row.HuCode)!))
            .ToList();
        var totalExcludingProtected = correctionRows.Sum(row => row.Qty)
                                      + _dataStore.GetLedgerBalance(request.ItemId, request.LocationId, null);

        if (protectedFilledPallets.Count > 0 && !StockQuantityRules.IsEffectivelyZero(totalExcludingProtected))
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "HU_BALANCE_CONTAINS_FILLED_PRODUCTION_PALLETS",
                "В остатках есть наполненные производственные паллеты. Автоматическая HU-корректировка остановлена, чтобы не обнулить реальный выпуск.",
                protectedFilledPallets,
                candidateBalances,
                totalAll,
                totalExcludingProtected);
        }

        if (!StockQuantityRules.IsEffectivelyZero(totalAll))
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "ITEM_LOCATION_TOTAL_NOT_ZERO",
                "Общий остаток не равен нулю. Нужна ручная корректировка по фактическому наличию.",
                protectedFilledPallets,
                candidateBalances,
                totalAll,
                totalExcludingProtected);
        }

        var rowsForCorrection = protectedFilledPallets.Count > 0 ? correctionRows : huRows;
        var hasPositive = rowsForCorrection.Any(row => row.Qty > StockQuantityRules.QtyTolerance);
        var hasNegative = rowsForCorrection.Any(row => StockQuantityRules.IsNegativeStockQty(row.Qty));
        if (!hasPositive || !hasNegative)
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "NO_OPPOSING_HU_BALANCES",
                "Нет одновременно положительных и отрицательных HU-остатков для авто-выравнивания.",
                protectedFilledPallets,
                candidateBalances,
                totalAll,
                totalExcludingProtected);
        }

        var userComment = string.IsNullOrWhiteSpace(request.Comment)
            ? "Сторно HU-разбалансировки"
            : request.Comment.Trim();
        var comment = BuildCorrectionComment(
            userComment,
            item.Name,
            location.Code,
            rowsForCorrection,
            protectedFilledPallets);

        var now = DateTime.Now;
        var docRef = _documents.GenerateDocRef(DocType.InventoryCorrection, now);
        var docId = _documents.CreateDoc(
            DocType.InventoryCorrection,
            docRef,
            comment,
            partnerId: null,
            orderRef: null,
            shippingRef: null,
            orderId: null,
            hydrateOrderLines: false);

        var lineCount = 0;
        foreach (var row in rowsForCorrection)
        {
            var correctionQty = -row.Qty;
            if (StockQuantityRules.IsEffectivelyZero(correctionQty))
            {
                continue;
            }

            _documents.AddDocLine(
                docId,
                request.ItemId,
                correctionQty,
                fromLocationId: null,
                toLocationId: request.LocationId,
                fromHu: null,
                toHu: row.HuCode.Trim());
            lineCount++;
        }

        if (lineCount == 0)
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "NO_HU_IMBALANCE",
                "Не удалось сформировать строки корректировки HU.",
                protectedFilledPallets,
                candidateBalances,
                totalAll,
                totalExcludingProtected);
        }

        return HuBalanceCorrectionDraftResult.Ok(
            docId,
            docRef,
            lineCount,
            "Создан черновик HU-корректировки. Проведите документ через стандартное закрытие.",
            protectedFilledPallets,
            candidateBalances,
            totalAll,
            totalExcludingProtected);
    }

    private static string NormalizeHu(string? huCode)
    {
        return string.IsNullOrWhiteSpace(huCode) ? string.Empty : huCode.Trim();
    }

    private static string BuildCorrectionComment(
        string userComment,
        string itemName,
        string locationCode,
        IReadOnlyList<HuStockRow> huRows,
        IReadOnlyList<ProtectedFilledProductionPallet> protectedFilledPallets)
    {
        var huSummary = string.Join(
            ", ",
            huRows.Select(row => $"{row.HuCode}={row.Qty:0.###}"));
        var parts = new List<string>
        {
            "HU-balance correction.",
            userComment,
            $"Item: {itemName}.",
            $"Location: {locationCode}.",
            $"HU balances: {huSummary}."
        };

        if (protectedFilledPallets.Count > 0)
        {
            var protectedSummary = string.Join(
                ", ",
                protectedFilledPallets.Select(pallet => $"{pallet.HuCode} (FILLED {pallet.PrdDocRef})"));
            parts.Add($"Protected FILLED pallets: {protectedSummary}.");
        }

        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}

public sealed class HuBalanceCorrectionDraftRequest
{
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public string? Comment { get; init; }
}

public sealed class HuBalanceCorrectionDraftResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public long? DocId { get; init; }
    public string? DocRef { get; init; }
    public int LineCount { get; init; }
    public IReadOnlyList<ProtectedFilledProductionPallet> ProtectedFilledPallets { get; init; } =
        Array.Empty<ProtectedFilledProductionPallet>();
    public IReadOnlyList<HuBalanceCorrectionCandidateBalance> CandidateBalances { get; init; } =
        Array.Empty<HuBalanceCorrectionCandidateBalance>();
    public double? TotalAll { get; init; }
    public double? TotalExcludingProtected { get; init; }

    public static HuBalanceCorrectionDraftResult Ok(
        long docId,
        string docRef,
        int lineCount,
        string message,
        IReadOnlyList<ProtectedFilledProductionPallet> protectedFilledPallets,
        IReadOnlyList<HuBalanceCorrectionCandidateBalance> candidateBalances,
        double totalAll,
        double totalExcludingProtected) =>
        new()
        {
            Success = true,
            DocId = docId,
            DocRef = docRef,
            LineCount = lineCount,
            Message = message,
            ProtectedFilledPallets = protectedFilledPallets,
            CandidateBalances = candidateBalances,
            TotalAll = totalAll,
            TotalExcludingProtected = totalExcludingProtected
        };

    public static HuBalanceCorrectionDraftResult Fail(
        string error,
        string message,
        IReadOnlyList<ProtectedFilledProductionPallet>? protectedFilledPallets = null,
        IReadOnlyList<HuBalanceCorrectionCandidateBalance>? candidateBalances = null,
        double? totalAll = null,
        double? totalExcludingProtected = null) =>
        new()
        {
            Success = false,
            Error = error,
            Message = message,
            ProtectedFilledPallets = protectedFilledPallets ?? Array.Empty<ProtectedFilledProductionPallet>(),
            CandidateBalances = candidateBalances ?? Array.Empty<HuBalanceCorrectionCandidateBalance>(),
            TotalAll = totalAll,
            TotalExcludingProtected = totalExcludingProtected
        };
}
