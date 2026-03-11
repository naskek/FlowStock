using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class UpsertSemanticsTests
{
    [Fact]
    public async Task SameDocUidNewEventIdRicherCompatibleHeader_PerformsAcceptedUpsert()
    {
        const string docUid = "draft-upsert-001";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-upsert-001",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-UPSERT-001",
                Comment = "TSD",
                DraftOnly = true
            });

        var upserted = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-upsert-002",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-UPSERT-001",
                Comment = "Header synced",
                PartnerId = 200,
                ToLocationId = 10,
                DraftOnly = false
            });

        Assert.NotNull(created.Doc);
        Assert.NotNull(upserted.Doc);
        Assert.Equal(created.Doc!.Id, upserted.Doc!.Id);
        Assert.Equal("DRAFT", upserted.Doc.Status);
        Assert.Equal(1, harness.DocCount);

        var doc = harness.GetDoc(created.Doc.Id);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Equal(200, doc.PartnerId);
        Assert.Equal("Header synced", doc.Comment);
        Assert.Null(doc.ClosedAt);

        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid));
        Assert.Equal(created.Doc.Id, docInfo.DocId);
        Assert.Equal(200, docInfo.PartnerId);
        Assert.Equal(10, docInfo.ToLocationId);
        Assert.Equal("DRAFT", docInfo.Status);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task SameDocUidConflictingIdentity_ReturnsDuplicateDocUid()
    {
        const string docUid = "draft-upsert-002";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-upsert-003",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-UPSERT-002",
                DraftOnly = true
            });

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-upsert-004",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-UPSERT-CHANGED",
                DraftOnly = true
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("DUPLICATE_DOC_UID", payload.Error);
        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", docUid));
    }

    [Fact]
    public async Task AcceptedUpsert_WritesApiEvent()
    {
        const string docUid = "draft-upsert-003";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-upsert-005",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-UPSERT-003",
                DraftOnly = true
            });

        var upserted = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-create-upsert-006",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-UPSERT-003",
                PartnerId = 200,
                ToLocationId = 10,
                DraftOnly = false
            });

        Assert.NotNull(created.Doc);
        Assert.NotNull(upserted.Doc);
        Assert.Equal(created.Doc!.Id, upserted.Doc!.Id);
        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);
        Assert.Equal(2, apiStore.CountEvents("DOC_CREATE", docUid));
        Assert.NotNull(apiStore.GetEvent("evt-create-upsert-006"));
        Assert.Empty(harness.LedgerEntries);
    }
}
