using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services.Marking;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingImportServiceTests
{
    [Fact]
    public void Import_DuplicateFile_ShortCircuitsBeforeTransaction()
    {
        var existingImport = new MarkingCodeImport
        {
            Id = Guid.NewGuid(),
            FileHash = "existing-hash",
            OriginalFilename = "existing.csv",
            StoragePath = "<memory>",
            SourceType = "csv",
            Status = MarkingCodeImportStatus.Bound,
            CreatedAt = DateTime.Now
        };

        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.FindMarkingCodeImportByHash(It.IsAny<string>())).Returns(existingImport);

        var service = new MarkingImportService(store.Object);

        var result = service.Import("code-1\ncode-2"u8.ToArray(), "codes.csv");

        Assert.Equal(MarkingImportDecisionType.DuplicateFile, result.Decision.DecisionType);
        Assert.Null(result.ParsedFile);
        Assert.Equal(0, result.PersistedCodeCount);
        store.Verify(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()), Times.Never);
    }

    [Fact]
    public void Import_WithoutExactRequestNumberMatch_GoesToManualReview()
    {
        var store = CreateTransactionalStoreMock();
        MarkingCodeImport? updatedImport = null;

        store.Setup(s => s.FindMarkingCodeImportByHash(It.IsAny<string>())).Returns((MarkingCodeImport?)null);
        store.Setup(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>())).Returns<MarkingCodeImport>(import => import.Id);
        store.Setup(s => s.UpdateMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Callback<MarkingCodeImport>(import => updatedImport = import);

        var service = new MarkingImportService(store.Object);

        var result = service.Import("code-1\ncode-2"u8.ToArray(), "codes.csv");

        Assert.Equal(MarkingImportDecisionType.ManualReview, result.Decision.DecisionType);
        Assert.Equal(0, result.PersistedCodeCount);
        Assert.Equal(0, result.SkippedExistingCodeCount);
        Assert.NotNull(result.ImportId);
        Assert.NotNull(updatedImport);
        Assert.Equal(MarkingCodeImportStatus.ManualReview, updatedImport!.Status);
        Assert.Equal(2, updatedImport.ValidCodeRows);
        store.Verify(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()), Times.Never);
        store.Verify(s => s.UpdateMarkingOrderStatus(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public void Import_ExactRequestNumberMatch_BindsToOrderAndPersistsCodes()
    {
        var orderId = Guid.NewGuid();
        var order = new MarkingOrder
        {
            Id = orderId,
            OrderId = 123,
            RequestNumber = "REQ-123",
            RequestedQuantity = 2,
            Status = MarkingOrderStatus.WaitingForCodes,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var store = CreateTransactionalStoreMock();
        MarkingCodeImport? updatedImport = null;
        IReadOnlyList<MarkingCode>? persistedCodes = null;

        store.Setup(s => s.FindMarkingCodeImportByHash(It.IsAny<string>())).Returns((MarkingCodeImport?)null);
        store.Setup(s => s.FindMarkingOrderByRequestNumber("REQ-123")).Returns(order);
        store.Setup(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>())).Returns<MarkingCodeImport>(import => import.Id);
        store.Setup(s => s.ExistsMarkingCodeByRaw(It.IsAny<string>())).Returns(false);
        store.Setup(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()))
            .Callback<IReadOnlyList<MarkingCode>>(codes => persistedCodes = codes);
        store.Setup(s => s.UpdateMarkingOrderStatus(orderId, MarkingOrderStatus.CodesBound, It.IsAny<DateTime?>(), It.IsAny<DateTime>()));
        store.Setup(s => s.UpdateMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Callback<MarkingCodeImport>(import => updatedImport = import);

        var service = new MarkingImportService(store.Object);

        var result = service.Import("code-1\ncode-2"u8.ToArray(), "incoming_request-REQ-123.csv");

        Assert.Equal(MarkingImportDecisionType.Bound, result.Decision.DecisionType);
        Assert.Equal(orderId, result.Decision.TargetMarkingOrderId);
        Assert.Equal(2, result.PersistedCodeCount);
        Assert.Equal(0, result.SkippedExistingCodeCount);
        Assert.NotNull(persistedCodes);
        Assert.Equal(2, persistedCodes!.Count);
        Assert.All(persistedCodes, code =>
        {
            Assert.Equal(orderId, code.MarkingOrderId);
            Assert.Equal(MarkingCodeStatus.Reserved, code.Status);
        });
        Assert.NotNull(updatedImport);
        Assert.Equal(MarkingCodeImportStatus.Bound, updatedImport!.Status);
        Assert.Equal(orderId, updatedImport.MatchedMarkingOrderId);
    }

    [Fact]
    public void Import_DuplicateCodesAlreadyInStorage_AreSkipped()
    {
        var orderId = Guid.NewGuid();
        var order = new MarkingOrder
        {
            Id = orderId,
            OrderId = 123,
            RequestNumber = "REQ-123",
            RequestedQuantity = 2,
            Status = MarkingOrderStatus.WaitingForCodes,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var store = CreateTransactionalStoreMock();
        MarkingCodeImport? updatedImport = null;
        IReadOnlyList<MarkingCode>? persistedCodes = null;

        store.Setup(s => s.FindMarkingCodeImportByHash(It.IsAny<string>())).Returns((MarkingCodeImport?)null);
        store.Setup(s => s.FindMarkingOrderByRequestNumber("REQ-123")).Returns(order);
        store.Setup(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>())).Returns<MarkingCodeImport>(import => import.Id);
        store.Setup(s => s.ExistsMarkingCodeByRaw("code-1")).Returns(true);
        store.Setup(s => s.ExistsMarkingCodeByRaw("code-2")).Returns(false);
        store.Setup(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()))
            .Callback<IReadOnlyList<MarkingCode>>(codes => persistedCodes = codes);
        store.Setup(s => s.UpdateMarkingOrderStatus(orderId, MarkingOrderStatus.CodesBound, It.IsAny<DateTime?>(), It.IsAny<DateTime>()));
        store.Setup(s => s.UpdateMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Callback<MarkingCodeImport>(import => updatedImport = import);

        var service = new MarkingImportService(store.Object);

        var result = service.Import("code-1\ncode-2"u8.ToArray(), "incoming_request-REQ-123.csv");

        Assert.Equal(MarkingImportDecisionType.Bound, result.Decision.DecisionType);
        Assert.Equal(1, result.PersistedCodeCount);
        Assert.Equal(1, result.SkippedExistingCodeCount);
        Assert.NotNull(persistedCodes);
        Assert.Single(persistedCodes!);
        Assert.Equal("code-2", persistedCodes[0].Code);
        Assert.NotNull(updatedImport);
        Assert.Equal(1, updatedImport!.DuplicateCodeRows);
    }

    [Fact]
    public void Import_WithNoValidRows_PersistsFailedImport()
    {
        var store = CreateTransactionalStoreMock();
        MarkingCodeImport? updatedImport = null;

        store.Setup(s => s.FindMarkingCodeImportByHash(It.IsAny<string>())).Returns((MarkingCodeImport?)null);
        store.Setup(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>())).Returns<MarkingCodeImport>(import => import.Id);
        store.Setup(s => s.UpdateMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Callback<MarkingCodeImport>(import => updatedImport = import);

        var service = new MarkingImportService(store.Object);

        var result = service.Import("\n \r\n\t"u8.ToArray(), "empty.csv");

        Assert.Equal(MarkingImportDecisionType.Failed, result.Decision.DecisionType);
        Assert.Equal(0, result.PersistedCodeCount);
        Assert.NotNull(result.ImportId);
        Assert.NotNull(updatedImport);
        Assert.Equal(MarkingCodeImportStatus.Failed, updatedImport!.Status);
        Assert.Equal(0, updatedImport.ValidCodeRows);
        store.Verify(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()), Times.Never);
    }

    private static Mock<IDataStore> CreateTransactionalStoreMock()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        return store;
    }
}
