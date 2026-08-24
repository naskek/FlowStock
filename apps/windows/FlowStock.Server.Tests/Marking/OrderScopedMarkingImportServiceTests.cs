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
    public void Preview_TwoOutstandingRequestsForSameGtin_IsAmbiguous()
    {
        var store = new Mock<IOrderScopedMarkingImportStore>(MockBehavior.Strict);
        store.Setup(value => value.GetRelatedOutstandingMarkingRequests(257))
            .Returns(new[] { RelatedRequest(GtinA, 1, 0), RelatedRequest(GtinA, 2, 0) });

        var result = new OrderScopedMarkingImportService(store.Object).Preview(
            257,
            new[] { File("codes.tsv", Dm(GtinA, "SERIAL-A")) });

        Assert.False(result.IsValid);
        Assert.Equal("AMBIGUOUS_REQUEST_SCOPE", result.ErrorCode);
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

    private static MarkingImportUploadFile File(string name, string dm) =>
        new(name, Encoding.UTF8.GetBytes($"\"{dm}\"\t{dm.Substring(2, 14)}\tProduct"));

    private static string Dm(string gtin, string serial) => $"01{gtin}21{serial}\u001D93VERIFY";
}
