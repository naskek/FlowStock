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

        var totalQty = huRows.Sum(row => row.Qty)
                       + _dataStore.GetLedgerBalance(request.ItemId, request.LocationId, null);
        if (!StockQuantityRules.IsEffectivelyZero(totalQty))
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "ITEM_LOCATION_TOTAL_NOT_ZERO",
                "Общий остаток не равен нулю. Нужна ручная корректировка по фактическому наличию.");
        }

        var hasPositive = huRows.Any(row => row.Qty > StockQuantityRules.QtyTolerance);
        var hasNegative = huRows.Any(row => StockQuantityRules.IsNegativeStockQty(row.Qty));
        if (!hasPositive || !hasNegative)
        {
            return HuBalanceCorrectionDraftResult.Fail(
                "NO_OPPOSING_HU_BALANCES",
                "Нет одновременно положительных и отрицательных HU-остатков для авто-выравнивания.");
        }

        var userComment = string.IsNullOrWhiteSpace(request.Comment)
            ? "Сторно HU-разбалансировки"
            : request.Comment.Trim();
        var comment = BuildCorrectionComment(
            userComment,
            item.Name,
            location.Code,
            huRows);

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
        foreach (var row in huRows)
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
                "Не удалось сформировать строки корректировки HU.");
        }

        return HuBalanceCorrectionDraftResult.Ok(
            docId,
            docRef,
            lineCount,
            "Создан черновик HU-корректировки. Проведите документ через стандартное закрытие.");
    }

    private static string BuildCorrectionComment(
        string userComment,
        string itemName,
        string locationCode,
        IReadOnlyList<HuStockRow> huRows)
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

    public static HuBalanceCorrectionDraftResult Ok(long docId, string docRef, int lineCount, string message) =>
        new()
        {
            Success = true,
            DocId = docId,
            DocRef = docRef,
            LineCount = lineCount,
            Message = message
        };

    public static HuBalanceCorrectionDraftResult Fail(string error, string message) =>
        new()
        {
            Success = false,
            Error = error,
            Message = message
        };
}
