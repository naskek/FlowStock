namespace FlowStock.Core.Abstractions;

/// <summary>Narrow server-authoritative seam for aggregate marking configuration.</summary>
public interface IMarkingAggregateStore
{
    IReadOnlyDictionary<long, MarkingLineAggregateCoverage> GetAggregateMarkingCoverageByOrderLine(long orderId);
    IReadOnlyDictionary<long, double> GetLegacyExemptQuantityByOrderLine(long orderId) =>
        new Dictionary<long, double>();
    IReadOnlyDictionary<long, double> GetActiveMarkingRequestScopeQuantityByItem(long orderId);
    int GetDefaultMarkingReserveQuantity();
    void SetDefaultMarkingReserveQuantity(int quantity, string actor, DateTime changedAt) =>
        throw new NotSupportedException("Marking settings are read-only in this store.");
    void CreateImmutableRequestScopes(
        Guid markingOrderId,
        long orderId,
        long itemId,
        string gtin,
        int requiredQuantity,
        DateTime createdAt);
    void RecordConfirmedRealImportAndActivateCoverage(
        Guid markingOrderId,
        Guid markingCodeImportId,
        string originalFilename,
        string fileHash,
        long fileSizeBytes,
        int rowCount,
        DateTime confirmedAt);
    void ValidateAndCreateReadyHuFacts(long productionReceiptDocId, DateTime createdAt);
    void SupersedeReadyHuFactsForCorrection(
        long sourceProductionReceiptDocId,
        long replacementPalletId,
        string correctionReference,
        DateTime changedAt);
}

public sealed record MarkingLineAggregateCoverage(
    double OperationalQuantity,
    double ReadyHuQuantity)
{
    public double TotalQuantity => OperationalQuantity + ReadyHuQuantity;
}
