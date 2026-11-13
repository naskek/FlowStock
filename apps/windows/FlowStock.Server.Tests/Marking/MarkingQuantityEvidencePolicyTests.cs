using FlowStock.Data;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingQuantityEvidencePolicyTests
{
    private static readonly Guid TaskId = Guid.Parse("21111111-1111-4111-8111-111111111111");
    private static readonly Guid PoolId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public void QuantityOnly_ExposesExactFreeAndReceiptBalances()
    {
        var result = MarkingQuantityEvidencePolicy.Evaluate(ValidQuantity());

        Assert.Equal(MarkingQuantityEvidenceState.VerifiedQuantity, result.State);
        Assert.True(result.HasVerifiedQuantity);
        Assert.Null(result.RefusalCode);
        Assert.Equal(42L, result.ItemId);
        Assert.Equal("04601234567890", result.NormalizedGtin);
        Assert.Equal("PRODUCTION_ORDER", result.SourceType);
        Assert.Equal(7L, result.OrderId);
        Assert.Equal(8L, result.OrderLineId);
        Assert.Equal(10, result.IssuedQuantity);
        Assert.Equal(5, result.AvailableQuantity);
        Assert.Equal(4, result.AppliedQuantity);
        Assert.Equal(1, result.VoidedQuantity);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(PoolId, receipt.PoolId);
        Assert.Equal(301, receipt.ReceiptDocId);
        Assert.Equal(302, receipt.ReceiptLineId);
        Assert.Equal(4, receipt.AppliedQuantity);
    }

    [Fact]
    public void MixedLegacyAndQuantity_IsBlockedEvenWhenLegacyVoided()
    {
        var snapshot = ValidQuantity();
        var legacy = new MarkingHybridCoverageRow(
            "PerCode", Guid.NewGuid().ToString(), "temporary-chz-export",
            "LegacySynthetic", "Voided", null, null, "04601234567890",
            8, 42, 7, 1, "ProvenTemporary");
        var decision = MarkingQuantityEvidencePolicy.Evaluate(snapshot with
        {
            Rows = snapshot.Rows.Append(legacy).ToArray(),
            PerCodeRows = 1,
            RequiresReconciliation = true,
            UnambiguousObservedActive = null
        });

        Assert.Equal(MarkingQuantityEvidenceState.Blocked, decision.State);
        Assert.Equal("MIXED_SOURCE", decision.RefusalCode);
        Assert.False(decision.HasVerifiedQuantity);
        Assert.Equal(0, decision.IssuedQuantity);
        Assert.Empty(decision.Receipts);
    }

    [Fact]
    public void AmbiguousPerCode_IsNeverClassifiedAsVerifiedLegacy()
    {
        var legacy = new MarkingHybridCoverageRow(
            "PerCode", Guid.NewGuid().ToString(), "csv",
            "HistoricalUnknown", "Reserved", null, null, "04601234567890",
            8, 42, 7, 1, "Unverified");
        var snapshot = new MarkingHybridCoverageSnapshot(
            TaskId, [legacy], 1, 0, 1, 0, 1, true, null);

        var decision = MarkingQuantityEvidencePolicy.Evaluate(snapshot);
        Assert.Equal(MarkingQuantityEvidenceState.Blocked, decision.State);
        Assert.Equal("UNVERIFIED_PER_CODE", decision.RefusalCode);
    }

    [Fact]
    public void ProvenLegacy_RemainsLegacyOnlyWithoutQuantityAuthorization()
    {
        var legacy = new MarkingHybridCoverageRow(
            "PerCode", Guid.NewGuid().ToString(), "temporary-chz-export",
            "LegacySynthetic", "Reserved", null, null, "04601234567890",
            8, 42, 7, 1, "ProvenTemporary");
        var snapshot = new MarkingHybridCoverageSnapshot(
            TaskId, [legacy], 1, 0, 1, 0, 1, false, 1);

        var decision = MarkingQuantityEvidencePolicy.Evaluate(snapshot);
        Assert.Equal(MarkingQuantityEvidenceState.LegacyOnly, decision.State);
        Assert.False(decision.HasVerifiedQuantity);
        Assert.Equal(0, decision.AvailableQuantity);
    }

    [Fact]
    public void QuantityScopeDrift_FailsClosed()
    {
        var snapshot = ValidQuantity();
        var corrupted = snapshot.Rows.ToArray();
        corrupted[1] = corrupted[1] with { OrderId = 99 };
        Assert.Equal("INVALID_QUANTITY_ROW",
            MarkingQuantityEvidencePolicy.Evaluate(snapshot with { Rows = corrupted }).RefusalCode);
    }

    [Fact]
    public void QuantityEvidenceDrift_FailsClosed()
    {
        var snapshot = ValidQuantity();
        var corrupted = snapshot.Rows.ToArray();
        corrupted[0] = corrupted[0] with { Evidence = "Unverified" };
        Assert.Equal("INVALID_QUANTITY_ROW",
            MarkingQuantityEvidencePolicy.Evaluate(snapshot with { Rows = corrupted }).RefusalCode);
    }

    [Fact]
    public void ReceiptWithoutDocAndLine_FailsClosed()
    {
        var snapshot = ValidQuantity();
        var corrupted = snapshot.Rows.ToArray();
        corrupted[1] = corrupted[1] with { ReceiptDocId = null };
        Assert.Equal("INVALID_RECEIPT_STATE",
            MarkingQuantityEvidencePolicy.Evaluate(snapshot with { Rows = corrupted }).RefusalCode);
    }

    [Fact]
    public void SnapshotAggregateDrift_FailsClosed()
    {
        var snapshot = ValidQuantity() with { QuantityIssued = 11 };
        Assert.Equal("BALANCE_MISMATCH",
            MarkingQuantityEvidencePolicy.Evaluate(snapshot).RefusalCode);
    }

    [Fact]
    public void DuplicatePoolStatusReceiptSlice_FailsClosed()
    {
        var snapshot = ValidQuantity();
        var repeated = snapshot.Rows.Append(snapshot.Rows[1]).ToArray();
        Assert.Equal("INVALID_QUANTITY_ROW",
            MarkingQuantityEvidencePolicy.Evaluate(snapshot with { Rows = repeated }).RefusalCode);
    }

    [Fact]
    public void Overflow_FailsClosedInsteadOfWrapping()
    {
        var snapshot = ValidQuantity();
        var rows = snapshot.Rows.ToArray();
        rows[0] = rows[0] with { Quantity = long.MaxValue };
        rows[1] = rows[1] with { Quantity = 1 };
        Assert.Equal("QUANTITY_OVERFLOW",
            MarkingQuantityEvidencePolicy.Evaluate(snapshot with { Rows = rows }).RefusalCode);
    }

    [Fact]
    public void EmptySnapshot_HasNoAuthorizationOrQuantity()
    {
        var snapshot = new MarkingHybridCoverageSnapshot(
            TaskId, [], 0, 0, 0, 0, 0, false, 0);
        var decision = MarkingQuantityEvidencePolicy.Evaluate(snapshot);
        Assert.Equal(MarkingQuantityEvidenceState.NoCoverage, decision.State);
        Assert.False(decision.HasVerifiedQuantity);
        Assert.Empty(decision.Receipts);
    }

    private static MarkingHybridCoverageSnapshot ValidQuantity()
    {
        var free = QuantityRow("Reserved", 5);
        var assigned = QuantityRow("Applied", 4, 301, 302);
        var voided = QuantityRow("Voided", 1);
        return new MarkingHybridCoverageSnapshot(
            TaskId, [free, assigned, voided],
            PerCodeRows: 0,
            QuantityIssued: 10,
            PerCodeObservedActive: 0,
            QuantityObservedActive: 9,
            ObservedPotentialActive: 9,
            RequiresReconciliation: false,
            UnambiguousObservedActive: 9);
    }

    private static MarkingHybridCoverageRow QuantityRow(
        string status, long quantity, long? receiptDoc = null, long? receiptLine = null) =>
        new(
            SourceFamily: "QuantityPool",
            SourceId: PoolId.ToString(),
            SourceType: "PRODUCTION_ORDER",
            Origin: "SyntheticQuantity",
            Status: status,
            ReceiptDocId: receiptDoc,
            ReceiptLineId: receiptLine,
            NormalizedGtin: "04601234567890",
            OrderLineId: 8,
            ItemId: 42,
            OrderId: 7,
            Quantity: quantity,
            Evidence: "NewQuantity");
}
