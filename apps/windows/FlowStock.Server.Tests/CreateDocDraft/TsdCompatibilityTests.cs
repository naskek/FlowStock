using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class TsdCompatibilityTests
{
    [Fact]
    public async Task TsdSkeletalCreate_ThenRicherUpsert_KeepsDocInDraft()
    {
        const string docUid = "tsd-draft-001";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-draft-001",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                Comment = "TSD",
                DraftOnly = true
            });

        var upserted = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-draft-002",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = created.Doc!.DocRef,
                Comment = "TSD header synced",
                PartnerId = 200,
                ToLocationId = 10,
                DraftOnly = false
            });

        Assert.NotNull(created.Doc);
        Assert.NotNull(upserted.Doc);
        Assert.Equal(created.Doc!.Id, upserted.Doc!.Id);

        var doc = harness.GetDoc(created.Doc.Id);
        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid));

        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Equal(200, doc.PartnerId);
        Assert.Equal("TSD header synced", doc.Comment);
        Assert.Equal("DRAFT", docInfo.Status);
        Assert.Equal(doc.Id, docInfo.DocId);
        Assert.Equal(10, docInfo.ToLocationId);
        Assert.NotNull(created.Doc.DocRef);
        Assert.Equal(created.Doc.DocRef, upserted.Doc!.DocRef);
        Assert.Empty(harness.LedgerEntries);
    }
}
