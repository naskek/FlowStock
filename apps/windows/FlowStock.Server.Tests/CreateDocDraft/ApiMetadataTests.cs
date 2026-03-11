using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class ApiMetadataTests
{
    [Fact]
    public async Task FirstCreate_WritesApiDocsAndApiEventsMetadata()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = "draft-meta-001",
                EventId = "evt-create-meta-001",
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = "IN-META-001",
                DraftOnly = true
            });

        Assert.NotNull(payload.Doc);
        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc("draft-meta-001"));
        Assert.Equal(payload.Doc!.Id, docInfo.DocId);
        Assert.Equal("DRAFT", docInfo.Status);
        Assert.Equal("IN-META-001", docInfo.DocRef);
        Assert.Equal("INBOUND", docInfo.DocType);
        Assert.NotNull(apiStore.GetEvent("evt-create-meta-001"));
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", "draft-meta-001"));
    }

    [Fact]
    public async Task Upsert_ReconcilesApiDocsHeaderMetadata()
    {
        const string docUid = "draft-meta-002";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-meta-002",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-META-002",
                DraftOnly = true
            });

        var upserted = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-meta-003",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-META-002",
                PartnerId = 200,
                ToLocationId = 10,
                ToHu = "HU-000001",
                DraftOnly = false
            });

        Assert.NotNull(created.Doc);
        Assert.NotNull(upserted.Doc);
        Assert.Equal(created.Doc!.Id, upserted.Doc!.Id);

        var doc = harness.GetDoc(created.Doc.Id);
        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid));

        Assert.Equal(200, docInfo.PartnerId);
        Assert.Equal(10, docInfo.ToLocationId);
        Assert.Equal("HU-000001", docInfo.ToHu);
        Assert.Equal("HU-000001", doc.ShippingRef);
        Assert.Equal("DRAFT", docInfo.Status);
    }

    [Fact]
    public async Task Upsert_DoesNotCreateSecondApiDocMapping()
    {
        const string docUid = "draft-meta-004";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-meta-004",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-META-004",
                DraftOnly = true
            });

        await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-meta-005",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-META-004",
                PartnerId = 200,
                ToLocationId = 10,
                DraftOnly = false
            });

        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);
    }
}
