using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class NegativeStockCorrectionService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;
    private readonly DocumentService _documents = new(dataStore);

    public IReadOnlyList<NegativeStockBalanceRow> GetNegativeBalances() =>
        _dataStore.GetNegativeStockBalances();

    public NegativeStockCorrectionDraftResult CreateCorrectionDraft(NegativeStockCorrectionDraftRequest request)
    {
        if (request.ItemId <= 0 || request.LocationId <= 0)
        {
            return NegativeStockCorrectionDraftResult.Fail("INVALID_KEYS", "Укажите товар и место хранения.");
        }

        if (request.QtyToCompensate <= StockQuantityRules.QtyTolerance)
        {
            return NegativeStockCorrectionDraftResult.Fail("INVALID_QTY", "Количество компенсации должно быть больше 0.");
        }

        var huCode = string.IsNullOrWhiteSpace(request.HuCode) ? null : request.HuCode.Trim();
        var currentQty = _dataStore.GetLedgerBalance(request.ItemId, request.LocationId, huCode);
        if (!StockQuantityRules.IsNegativeStockQty(currentQty))
        {
            return NegativeStockCorrectionDraftResult.Fail(
                "NOT_NEGATIVE",
                "По указанному товару, месту и HU нет отрицательного остатка в ledger.");
        }

        var maxCompensation = Math.Abs(currentQty);
        if (request.QtyToCompensate > maxCompensation + StockQuantityRules.QtyTolerance)
        {
            return NegativeStockCorrectionDraftResult.Fail(
                "EXCEEDS_NEGATIVE",
                $"Количество компенсации не может превышать {maxCompensation:0.###} (текущий минус).");
        }

        var item = _dataStore.FindItemById(request.ItemId);
        if (item == null)
        {
            return NegativeStockCorrectionDraftResult.Fail("ITEM_NOT_FOUND", "Товар не найден.");
        }

        var location = _dataStore.FindLocationById(request.LocationId);
        if (location == null)
        {
            return NegativeStockCorrectionDraftResult.Fail("LOCATION_NOT_FOUND", "Место хранения не найдено.");
        }

        string? sourceDocRef = null;
        DocType? sourceDocType = null;
        if (request.SourceDocId.HasValue)
        {
            var sourceDoc = _dataStore.GetDoc(request.SourceDocId.Value);
            if (sourceDoc == null)
            {
                return NegativeStockCorrectionDraftResult.Fail("SOURCE_DOC_NOT_FOUND", "Исходный документ не найден.");
            }

            if (sourceDoc.Status != DocStatus.Closed)
            {
                return NegativeStockCorrectionDraftResult.Fail(
                    "SOURCE_DOC_NOT_CLOSED",
                    "Исправлять можно только на основании проведённого документа.");
            }

            sourceDocRef = sourceDoc.DocRef;
            sourceDocType = sourceDoc.Type;
        }

        var userComment = string.IsNullOrWhiteSpace(request.Comment)
            ? "Сторно ошибочного отрицательного остатка"
            : request.Comment.Trim();
        var comment = BuildCorrectionComment(
            userComment,
            sourceDocRef,
            request.SourceLedgerEntryId,
            huCode,
            location.Code);

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

        _documents.AddDocLine(
            docId,
            request.ItemId,
            request.QtyToCompensate,
            fromLocationId: null,
            toLocationId: request.LocationId,
            fromHu: null,
            toHu: huCode);

        return NegativeStockCorrectionDraftResult.Ok(
            docId,
            docRef,
            "Создан черновик корректировки. Проведите документ через стандартное закрытие.");
    }

    private static string BuildCorrectionComment(
        string userComment,
        string? sourceDocRef,
        long? sourceLedgerEntryId,
        string? huCode,
        string locationCode)
    {
        var parts = new List<string>
        {
            "Корректировка отрицательного остатка.",
            userComment
        };

        if (!string.IsNullOrWhiteSpace(sourceDocRef))
        {
            parts.Add($"Source doc: {sourceDocRef}.");
        }

        if (sourceLedgerEntryId.HasValue)
        {
            parts.Add($"Source ledger: {sourceLedgerEntryId.Value}.");
        }

        if (!string.IsNullOrWhiteSpace(huCode))
        {
            parts.Add($"HU: {huCode}.");
        }

        parts.Add($"Location: {locationCode}.");
        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}

public sealed class NegativeStockCorrectionDraftRequest
{
    public long ItemId { get; init; }
    public long LocationId { get; init; }
    public string? HuCode { get; init; }
    public double QtyToCompensate { get; init; }
    public long? SourceDocId { get; init; }
    public long? SourceLedgerEntryId { get; init; }
    public string? Comment { get; init; }
}

public sealed class NegativeStockCorrectionDraftResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public long? DocId { get; init; }
    public string? DocRef { get; init; }

    public static NegativeStockCorrectionDraftResult Ok(long docId, string docRef, string message) =>
        new()
        {
            Success = true,
            DocId = docId,
            DocRef = docRef,
            Message = message
        };

    public static NegativeStockCorrectionDraftResult Fail(string error, string message) =>
        new()
        {
            Success = false,
            Error = error,
            Message = message
        };
}
