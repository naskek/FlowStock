using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IHuTransactionLockStore
{
    void LockNormalizedHus(IReadOnlyCollection<string> normalizedHus);
    void LockDocumentsForUpdate(IReadOnlyCollection<long> documentIds);
}

public interface ILedgerEntryIdStore
{
    long AddLedgerEntryReturningId(LedgerEntry entry);
}

public interface ILineScopedMarkingCodeStore
{
    int CountAvailableProductionMarkingCodesForReceipt(
        long? sourceOrderId,
        long itemId,
        string? gtin,
        long? orderLineId);
    IReadOnlyList<Guid> GetAvailableProductionMarkingCodeIdsForReceipt(
        long? sourceOrderId,
        long itemId,
        string? gtin,
        int take,
        long? orderLineId);
}

public interface IProductionPalletFillingCorrectionStore : IHuTransactionLockStore
{
    ProductionPalletFillingAdjustment? GetFillingAdjustment(Guid requestId);
    ProductionPalletFillingAdjustment? GetPredecessorFillingAdjustment(long sourcePalletId);
    bool TryClaimFillingAdjustment(
        Guid requestId,
        string payloadHash,
        string action,
        string reasonCode,
        string reasonText,
        string? actorName,
        string? deviceName,
        string? clientName,
        string? clientVersion,
        DateTime createdAt,
        out long adjustmentId);
    void CompleteFillingAdjustment(
        long adjustmentId,
        long sourcePalletId,
        long rootPalletId,
        long sourcePrdDocId,
        long? corDocId,
        long? replacementPalletId,
        long? replacementPrdDocId,
        long? predecessorAdjustmentId,
        string resultJson);
    IReadOnlyList<ProductionPalletFillingCorrectionHistoryEntry> GetFillingCorrectionHistory(string normalizedHu);
    IReadOnlyList<LedgerEntry> GetLedgerEntriesForHu(string normalizedHu);
    bool HasActiveReservationForHu(string normalizedHu);
    bool HasActiveDraftReference(string normalizedHu, long? excludedDocId);
    IReadOnlyList<ProductionPalletCorrectionMarkingCode> LockReceiptMarkingCodes(long sourcePrdDocId);
    int RollbackReceiptMarkingCodes(
        long adjustmentId,
        long sourcePrdDocId,
        long corDocId,
        IReadOnlyList<ProductionPalletCorrectionMarkingCode> codes,
        string reasonText,
        string? actorName,
        string? deviceName,
        DateTime changedAt);
    void MarkProductionPalletCorrected(long palletId);
    void ResetPartialProductionPallet(long palletId);
    ProductionPalletReplacementResult CreateReplacementProductionPallet(
        long sourcePalletId,
        long targetPrdDocId,
        IReadOnlyDictionary<long, long> replacementDocLineIdBySourceComponentId,
        DateTime createdAt);
    void RecalculateProductionPalletNumbers(long prdDocId);
    void AddFillingAdjustmentLedgerLine(
        long adjustmentId,
        ProductionPalletFillingCorrectionLedgerLine source,
        long corDocLineId,
        long generatedLedgerEntryId);
    void AddFillingAdjustmentComponentLine(
        long adjustmentId,
        string lineKind,
        ProductionPalletComponentLine source,
        long? replacementDocLineId,
        long? replacementComponentId);
}
