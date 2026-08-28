using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services.Marking;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderScopedMarkingImportServiceTests
{
    private const string GtinA = "04601234567890";
    private const string GtinB = "04601234567891";

    [Fact]
    public void Preview_MapsMultiFileMultiGtinBatchByRelatedScopes_NotByFilename()
    {
        var requestA = RelatedRequest(GtinA, required: 1, reserve: 0);
        var requestB = RelatedRequest(GtinB, required: 1, reserve: 0);
        var store = new Mock<IOrderScopedMarkingImportStore>(MockBehavior.Strict);
        store.Setup(value => value.GetRelatedOutstandingMarkingRequests(257))
            .Returns(new[] { requestA, requestB });
        store.Setup(value => value.FindExistingRealMarkingCodeHashes(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new HashSet<string>());

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            new[]
            {
                File("unrelated-name-one.tsv", Dm(GtinA, "SERIAL-A")),
                File("also-not-a-request-number.tsv", Dm(GtinB, "SERIAL-B"))
            });

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(2, result.Requests.Count);
        Assert.Contains(result.Requests, row => row.MarkingOrderId == requestA.MarkingOrderId && row.ValidInBatch == 1);
        Assert.Contains(result.Requests, row => row.MarkingOrderId == requestB.MarkingOrderId && row.ValidInBatch == 1);
        Assert.False(result.RequiresRecoveryConfirmation);
        Assert.NotEmpty(result.SnapshotHash);
    }

    [Fact]
    public void Preview_TwoOutstandingRequestsForSameGtin_AllocatesDeterministically()
    {
        var store = new Mock<IOrderScopedMarkingImportStore>(MockBehavior.Strict);
        var first = RelatedRequest(GtinA, 1, 0) with { CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var second = RelatedRequest(GtinA, 2, 0) with { CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        store.Setup(value => value.GetRelatedOutstandingMarkingRequests(257)).Returns(new[] { first, second });
        store.Setup(value => value.FindExistingRealMarkingCodeHashes(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new HashSet<string>());

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            new[]
            {
                File("codes-1.tsv", Dm(GtinA, "SERIAL-A")),
                File("codes-2.tsv", Dm(GtinA, "SERIAL-B")),
                File("codes-3.tsv", Dm(GtinA, "SERIAL-C"))
            });

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(1, result.Requests.Single(row => row.MarkingOrderId == first.MarkingOrderId).ValidInBatch);
        Assert.Equal(2, result.Requests.Single(row => row.MarkingOrderId == second.MarkingOrderId).ValidInBatch);
    }

    [Fact]
    public void Preview_SameGtinPartialImportThenIncrease_ClosesOldDeficitBeforeDelta()
    {
        var first = RelatedRequest(GtinA, 3, 0) with
        {
            ImportedQuantity = 1,
            ActiveScopedQuantity = 3,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var delta = RelatedRequest(GtinA, 2, 0) with
        {
            ActiveScopedQuantity = 2,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        var store = CreatePreviewStore(first, delta);

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            Enumerable.Range(1, 4)
                .Select(index => File($"codes-{index}.tsv", Dm(GtinA, $"SERIAL-{index}")))
                .ToArray());

        Assert.True(result.IsValid, result.Message);
        Assert.False(result.RequiresRecoveryConfirmation);
        Assert.Equal(2, result.Requests.Single(row => row.MarkingOrderId == first.MarkingOrderId).ValidInBatch);
        Assert.Equal(2, result.Requests.Single(row => row.MarkingOrderId == delta.MarkingOrderId).ValidInBatch);
    }

    [Fact]
    public void Preview_SameGtinRequiredCompleteReserveShort_PrioritizesNewDelta()
    {
        var first = RelatedRequest(GtinA, 2, 5) with
        {
            ImportedQuantity = 2,
            ActiveScopedQuantity = 2,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var delta = RelatedRequest(GtinA, 2, 5) with
        {
            ActiveScopedQuantity = 2,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        var store = CreatePreviewStore(first, delta);

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            new[]
            {
                File("delta-1.tsv", Dm(GtinA, "DELTA-1")),
                File("delta-2.tsv", Dm(GtinA, "DELTA-2"))
            });

        Assert.True(result.IsValid, result.Message);
        Assert.False(result.RequiresRecoveryConfirmation);
        Assert.Equal(0, result.Requests.Single(row => row.MarkingOrderId == first.MarkingOrderId).ValidInBatch);
        Assert.Equal(2, result.Requests.Single(row => row.MarkingOrderId == delta.MarkingOrderId).ValidInBatch);
    }

    [Fact]
    public void Preview_PartiallyRetiredRequest_UsesOnlyActiveRemainder()
    {
        var request = RelatedRequest(GtinA, 5, 5) with
        {
            ImportedQuantity = 2,
            ActiveScopedQuantity = 3
        };
        var store = CreatePreviewStore(request);

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            new[] { File("remainder.tsv", Dm(GtinA, "REMAINDER")) });

        var preview = Assert.Single(result.Requests);
        Assert.True(result.IsValid, result.Message);
        Assert.Equal(3, preview.OperationalRequiredQuantity);
        Assert.Equal(1, preview.ValidInBatch);
        Assert.True(preview.CoverageWillActivate);
        Assert.True(preview.ReserveShort);
        Assert.False(result.RequiresRecoveryConfirmation);
    }

    [Fact]
    public void Preview_FullyRetiredHistoricalSameGtinRequest_DoesNotCreateAmbiguityOrConsumeCodes()
    {
        var retired = RelatedRequest(GtinA, 10, 5) with
        {
            ActiveScopedQuantity = 0,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var current = RelatedRequest(GtinA, 1, 0) with
        {
            ActiveScopedQuantity = 1,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var store = CreatePreviewStore(retired, current);

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            new[] { File("current.tsv", Dm(GtinA, "CURRENT")) });

        var preview = Assert.Single(result.Requests);
        Assert.True(result.IsValid, result.Message);
        Assert.Equal(current.MarkingOrderId, preview.MarkingOrderId);
        Assert.Equal(1, preview.ValidInBatch);
    }

    [Fact]
    public void Confirm_InsufficientBatch_RequiresExplicitRecovery()
    {
        var request = RelatedRequest(GtinA, required: 2, reserve: 5);
        var store = new Mock<IOrderScopedMarkingImportStore>(MockBehavior.Strict);
        store.Setup(value => value.GetRelatedOutstandingMarkingRequests(257)).Returns(new[] { request });
        store.Setup(value => value.FindExistingRealMarkingCodeHashes(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new HashSet<string>());
        store.Setup(value => value.FindConfirmedOrderScopedMarkingImport(
                257, It.IsAny<Guid>(), "recovery-1", It.IsAny<string>()))
            .Returns((OrderScopedMarkingImportConfirmResult?)null);
        var service = new OrderScopedMarkingImportService(store.Object);
        var files = new[] { File("codes.tsv", Dm(GtinA, "SERIAL-A")) };
        var preview = service.Preview(257, files);
        var batchId = Guid.NewGuid();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Confirm(
            257, batchId, preview.SnapshotHash, "recovery-1", confirmRecovery: false, files));

        Assert.Equal("MARKING_IMPORT_RECOVERY_CONFIRMATION_REQUIRED", exception.Message);
        store.Verify(
            value => value.ConfirmOrderScopedMarkingImport(It.IsAny<OrderScopedMarkingImportConfirmCommand>()),
            Times.Never);

        store.Setup(value => value.ConfirmOrderScopedMarkingImport(
                It.Is<OrderScopedMarkingImportConfirmCommand>(command =>
                    command.ConfirmRecovery
                    && command.Codes.Count == 1
                    && command.Requests.Single().CoverageWillActivate == false)))
            .Returns(new OrderScopedMarkingImportConfirmResult(batchId, false, 1, Array.Empty<Guid>()));

        var recovered = service.Confirm(
            257, batchId, preview.SnapshotHash, "recovery-1", confirmRecovery: true, files);

        Assert.Equal(1, recovered.PersistedCodeCount);
        Assert.Empty(recovered.ActivatedMarkingOrderIds);
    }

    private static RelatedMarkingRequest RelatedRequest(string gtin, int required, int reserve) =>
        new(Guid.NewGuid(), $"REQ-{Guid.NewGuid():N}", gtin, required, reserve, required + reserve, 0, Guid.NewGuid().ToString("N"));

    private static Mock<IOrderScopedMarkingImportStore> CreatePreviewStore(
        params RelatedMarkingRequest[] requests)
    {
        var store = new Mock<IOrderScopedMarkingImportStore>(MockBehavior.Strict);
        store.Setup(value => value.GetRelatedOutstandingMarkingRequests(257)).Returns(requests);
        store.Setup(value => value.FindExistingRealMarkingCodeHashes(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new HashSet<string>());
        return store;
    }

    private static MarkingImportUploadFile File(string name, string dm) =>
        new(name, Encoding.UTF8.GetBytes($"\"{dm}\"\t{dm.Substring(2, 14)}\tProduct"));

    private static string Dm(string gtin, string serial) => $"01{gtin}21{serial}\u001D93VERIFY";
}
