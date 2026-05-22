using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class CanonicalCreateIntegrationTests
{
    [Fact]
    public async Task FirstCreateWithNewDocUid_CreatesOneDraftDoc()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "draft-create-001",
                EventId = "evt-create-001",
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = "IN-TEST-001",
                DraftOnly = true
            });

        Assert.True(payload.Ok);
        Assert.NotNull(payload.Doc);
        Assert.Equal(1, harness.DocCount);

        var doc = harness.GetDoc(payload.Doc!.Id);
        Assert.Equal("draft-create-001", payload.Doc.DocUid);
        Assert.Equal("IN-TEST-001", payload.Doc.DocRef);
        Assert.Equal("DRAFT", payload.Doc.Status);
        Assert.Equal("INBOUND", payload.Doc.Type);
        Assert.False(payload.Doc.DocRefChanged);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Equal(DocType.Inbound, doc.Type);
        Assert.Equal("IN-TEST-001", doc.DocRef);
    }

    [Fact]
    public async Task FirstCreate_WritesApiDocsMapping()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "draft-create-002",
                EventId = "evt-create-002",
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = "IN-TEST-002",
                DraftOnly = true
            });

        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc("draft-create-002"));
        Assert.Equal(payload.Doc!.Id, docInfo.DocId);
        Assert.Equal("DRAFT", docInfo.Status);
        Assert.Equal("IN-TEST-002", docInfo.DocRef);
        Assert.Equal("INBOUND", docInfo.DocType);
    }

    [Fact]
    public async Task Create_DoesNotWriteLedger()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "draft-create-003",
                EventId = "evt-create-003",
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = "IN-TEST-003",
                DraftOnly = true
            });

        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task MissingDocRef_GeneratesServerDocRef()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "draft-create-004",
                EventId = "evt-create-004",
                DeviceId = "API-01",
                Type = "INBOUND",
                DraftOnly = true
            });

        Assert.NotNull(payload.Doc);
        Assert.Matches(@"^IN-\d{4}-\d{6}$", payload.Doc!.DocRef ?? string.Empty);
        Assert.False(payload.Doc.DocRefChanged);
        Assert.NotNull(harness.FindDocByRef(payload.Doc.DocRef!));
    }

    [Fact]
    public async Task CollidingRequestedDocRef_ReturnsReplacementAndDocRefChanged()
    {
        const string requestedRef = "IN-COLLIDE-001";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateDocRefCollisionScenario(requestedRef);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "draft-create-005",
                EventId = "evt-create-005",
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = requestedRef,
                DraftOnly = true
            });

        Assert.NotNull(payload.Doc);
        Assert.True(payload.Doc!.DocRefChanged);
        Assert.NotEqual(requestedRef, payload.Doc.DocRef);
        Assert.Matches(@"^IN-\d{4}-\d{6}$", payload.Doc.DocRef ?? string.Empty);
        Assert.Equal(2, harness.DocCount);
    }

    [Fact]
    public async Task ProductionReceiptCreate_WithOrderNeed_HydratesLinesInsteadOfOpeningEmptyPrd()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        harness.SeedLocation(new Location { Id = 10, Code = "FG", Name = "Готовая продукция" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "INT-010",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 22, 10, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 1000,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedOrderReceiptRemaining(10, new OrderReceiptLine
        {
            OrderLineId = 1000,
            OrderId = 10,
            ItemId = 100,
            ItemName = "Товар",
            QtyOrdered = 5,
            QtyReceived = 0,
            QtyRemaining = 5,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "prd-create-with-need-001",
                EventId = "evt-prd-create-with-need-001",
                DeviceId = "WPF-01",
                Type = "PRODUCTION_RECEIPT",
                DocRef = "PRD-TEST-WITH-NEED",
                OrderId = 10,
                ToLocationId = 10
            });

        Assert.True(payload.Ok);
        var line = Assert.Single(harness.GetDocLines(payload.Doc!.Id));
        Assert.Equal(1000, line.OrderLineId);
        Assert.Equal(100, line.ItemId);
        Assert.Equal(5, line.Qty);
    }
}
