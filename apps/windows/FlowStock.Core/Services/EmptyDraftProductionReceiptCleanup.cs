using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class EmptyDraftProductionReceiptCleanup
{
    public sealed class CleanupResult
    {
        public long DocId { get; init; }
        public bool Deleted { get; init; }
        public string? SkipReasonCode { get; init; }
    }

    public static IReadOnlyList<CleanupResult> CleanupEmptyDraftProductionReceiptsForOrder(IDataStore store, long orderId)
    {
        var results = new List<CleanupResult>();
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt)
                     .OrderByDescending(doc => doc.Id))
        {
            results.Add(TryDeleteEmptyDraftProductionReceiptIfSafe(store, orderId, doc.Id));
        }

        return results;
    }

    public static CleanupResult TryDeleteEmptyDraftProductionReceiptIfSafe(
        IDataStore store,
        long orderId,
        long prdDocId)
    {
        var doc = store.GetDoc(prdDocId);
        if (doc == null)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "DOC_NOT_FOUND" };
        }

        if (doc.Type != DocType.ProductionReceipt)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "NOT_PRODUCTION_RECEIPT" };
        }

        if (doc.Status == DocStatus.Closed)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "DOC_CLOSED" };
        }

        if (doc.Status != DocStatus.Draft)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "DOC_NOT_DRAFT" };
        }

        if (doc.OrderId != orderId)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "ORDER_MISMATCH" };
        }

        if (store.CountLedgerEntriesByDocId(prdDocId) > 0)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "HAS_LEDGER" };
        }

        if (store.HasProductionPallets(prdDocId))
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "HAS_ACTIVE_PALLETS" };
        }

        if (store.GetDocLines(prdDocId).Count > 0)
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "HAS_DOC_LINES" };
        }

        if (store.HasProductionPalletLinesForDoc(prdDocId))
        {
            return new CleanupResult { DocId = prdDocId, Deleted = false, SkipReasonCode = "HAS_PALLET_LINES" };
        }

        store.DeleteDoc(prdDocId);
        return new CleanupResult { DocId = prdDocId, Deleted = true };
    }
}
