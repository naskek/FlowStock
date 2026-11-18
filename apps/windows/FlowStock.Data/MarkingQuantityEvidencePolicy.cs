namespace FlowStock.Data;

/// <summary>
/// Verified quantity facts for future transactional marking consumers.
/// This is NOT permission to export, assign, close PRD or correct an HU:
/// those commands must separately lock and revalidate their live source data.
/// </summary>
public static class MarkingQuantityEvidencePolicy
{
    public static MarkingQuantityEvidenceDecision Evaluate(MarkingHybridCoverageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        MarkingQuantityEvidenceDecision Result(
            MarkingQuantityEvidenceState state,
            string? refusal = null,
            long? itemId = null,
            string? gtin = null,
            string? sourceType = null,
            long? orderId = null,
            long? orderLineId = null,
            long issued = 0,
            long available = 0,
            long applied = 0,
            long voided = 0,
            IReadOnlyList<MarkingQuantityReceiptEvidence>? receipts = null) =>
            new(state, snapshot.MarkingOrderId, refusal, itemId, gtin, sourceType,
                orderId, orderLineId, issued, available, applied, voided,
                receipts ?? Array.Empty<MarkingQuantityReceiptEvidence>());

        if (snapshot.MarkingOrderId == Guid.Empty)
            return Result(MarkingQuantityEvidenceState.Blocked, "INVALID_TASK");

        if (snapshot.Rows.Count == 0)
            return snapshot.PerCodeRows == 0 && snapshot.QuantityIssued == 0
                   && snapshot.PerCodeObservedActive == 0
                   && snapshot.QuantityObservedActive == 0
                   && snapshot.ObservedPotentialActive == 0
                   && snapshot.UnambiguousObservedActive == 0
                   && !snapshot.RequiresReconciliation
                ? Result(MarkingQuantityEvidenceState.NoCoverage)
                : Result(MarkingQuantityEvidenceState.Blocked, "EMPTY_SNAPSHOT_MISMATCH");

        var hasPerCode = snapshot.Rows.Any(row => row.SourceFamily == "PerCode");
        var hasQuantity = snapshot.Rows.Any(row => row.SourceFamily == "QuantityPool");
        if (snapshot.Rows.Any(row => row.SourceFamily is not ("PerCode" or "QuantityPool")))
            return Result(MarkingQuantityEvidenceState.Blocked, "UNKNOWN_SOURCE");

        // A voided or quarantined legacy row still means the same task has
        // a historical per-code source. No source ID proves independent qty.
        if (hasPerCode && hasQuantity)
            return Result(MarkingQuantityEvidenceState.Blocked, "MIXED_SOURCE");

        if (hasPerCode)
        {
            if (snapshot.Rows.Any(row => row.Evidence is not ("ProvenTemporary" or "RealCode"))
                || snapshot.RequiresReconciliation)
                return Result(MarkingQuantityEvidenceState.Blocked, "UNVERIFIED_PER_CODE");

            // Per-code coverage is NOT authorized here, but even the
            // LegacyOnly label must never bless a corrupted diagnostic.
            long perCodeRows = 0;
            long perCodeActive = 0;
            try
            {
                foreach (var row in snapshot.Rows)
                {
                    if (row.Quantity <= 0)
                        return Result(MarkingQuantityEvidenceState.Blocked, "INVALID_PER_CODE_ROW");
                    perCodeRows = checked(perCodeRows + row.Quantity);
                    if (row.Status is "Reserved" or "Applied")
                        perCodeActive = checked(perCodeActive + row.Quantity);
                }
            }
            catch (OverflowException)
            {
                return Result(MarkingQuantityEvidenceState.Blocked, "PER_CODE_OVERFLOW");
            }

            if (snapshot.PerCodeRows != perCodeRows
                || snapshot.PerCodeObservedActive != perCodeActive
                || snapshot.QuantityIssued != 0
                || snapshot.QuantityObservedActive != 0
                || snapshot.ObservedPotentialActive != perCodeActive
                || snapshot.UnambiguousObservedActive != perCodeActive)
                return Result(MarkingQuantityEvidenceState.Blocked, "PER_CODE_SNAPSHOT_MISMATCH");

            return Result(MarkingQuantityEvidenceState.LegacyOnly);
        }

        if (!hasQuantity || snapshot.RequiresReconciliation)
            return Result(MarkingQuantityEvidenceState.Blocked, "UNVERIFIED_QUANTITY");

        var first = snapshot.Rows[0];
        if (first.ItemId is not > 0 || string.IsNullOrWhiteSpace(first.NormalizedGtin)
            || first.NormalizedGtin != first.NormalizedGtin.Trim()
            || first.SourceType is not ("PRODUCTION_ORDER" or "PRODUCTION_NEED"))
            return Result(MarkingQuantityEvidenceState.Blocked, "INVALID_SCOPE");

        long issued = 0;
        long available = 0;
        long applied = 0;
        long voided = 0;
        var receipts = new List<MarkingQuantityReceiptEvidence>();
        var seen = new HashSet<(Guid Pool, string Status, long? Doc, long? Line)>();
        try
        {
            foreach (var row in snapshot.Rows)
            {
                if (row.SourceFamily != "QuantityPool"
                    || row.Evidence != "NewQuantity"
                    || row.Origin != "SyntheticQuantity"
                    || row.SourceType != first.SourceType
                    || row.ItemId != first.ItemId
                    || row.OrderId != first.OrderId
                    || row.OrderLineId != first.OrderLineId
                    || row.NormalizedGtin != first.NormalizedGtin
                    || row.Quantity <= 0
                    || !Guid.TryParse(row.SourceId, out var poolId)
                    || poolId == Guid.Empty
                    || !seen.Add((poolId, row.Status, row.ReceiptDocId, row.ReceiptLineId)))
                    return Result(MarkingQuantityEvidenceState.Blocked, "INVALID_QUANTITY_ROW");

                issued = checked(issued + row.Quantity);
                switch (row.Status)
                {
                    case "Reserved" when row.ReceiptDocId is null && row.ReceiptLineId is null:
                        available = checked(available + row.Quantity);
                        break;
                    case "Voided" when row.ReceiptDocId is null && row.ReceiptLineId is null:
                        voided = checked(voided + row.Quantity);
                        break;
                    case "Applied" when row.ReceiptDocId is > 0 && row.ReceiptLineId is > 0:
                        applied = checked(applied + row.Quantity);
                        receipts.Add(new MarkingQuantityReceiptEvidence(
                            poolId, row.ReceiptDocId.Value, row.ReceiptLineId.Value, row.Quantity));
                        break;
                    default:
                        return Result(MarkingQuantityEvidenceState.Blocked, "INVALID_RECEIPT_STATE");
                }
            }

            var active = checked(available + applied);
            if (snapshot.PerCodeRows != 0 || snapshot.PerCodeObservedActive != 0
                || snapshot.QuantityIssued != issued
                || snapshot.QuantityObservedActive != active
                || snapshot.ObservedPotentialActive != active
                || snapshot.UnambiguousObservedActive != active
                || checked(active + voided) != issued)
                return Result(MarkingQuantityEvidenceState.Blocked, "BALANCE_MISMATCH");
        }
        catch (OverflowException)
        {
            return Result(MarkingQuantityEvidenceState.Blocked, "QUANTITY_OVERFLOW");
        }

        // Caller can use only these exact, typed facts; no aggregate of mixed
        // sources and no permission to mutate is ever returned.
        return Result(MarkingQuantityEvidenceState.VerifiedQuantity,
            itemId: first.ItemId,
            gtin: first.NormalizedGtin,
            sourceType: first.SourceType,
            orderId: first.OrderId,
            orderLineId: first.OrderLineId,
            issued: issued,
            available: available,
            applied: applied,
            voided: voided,
            receipts: receipts);
    }
}

public enum MarkingQuantityEvidenceState
{
    NoCoverage,
    LegacyOnly,
    VerifiedQuantity,
    Blocked
}

public sealed record MarkingQuantityReceiptEvidence(
    Guid PoolId,
    long ReceiptDocId,
    long ReceiptLineId,
    long AppliedQuantity);

public sealed record MarkingQuantityEvidenceDecision(
    MarkingQuantityEvidenceState State,
    Guid MarkingOrderId,
    string? RefusalCode,
    long? ItemId,
    string? NormalizedGtin,
    string? SourceType,
    long? OrderId,
    long? OrderLineId,
    long IssuedQuantity,
    long AvailableQuantity,
    long AppliedQuantity,
    long VoidedQuantity,
    IReadOnlyList<MarkingQuantityReceiptEvidence> Receipts)
{
    public bool HasVerifiedQuantity => State == MarkingQuantityEvidenceState.VerifiedQuantity;
}
