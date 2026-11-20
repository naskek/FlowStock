namespace FlowStock.Core.Abstractions;

// Dormant adopted-source COR protocol; callers must use the common correction transaction.
public interface IProductionReceiptHuCorrectionStore
{
    bool IsConsumedHuReceiptCorrectionProven(long sourceDocId);
    void PrepareHuReceiptCorrection(long adjustmentId, long sourcePalletId, long replacementPalletId, long corDocId);
    void FinishHuReceiptCorrection(long adjustmentId);
}
