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
}
