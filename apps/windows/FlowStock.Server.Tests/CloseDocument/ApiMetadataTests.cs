using System.Net;
using System.Net.Http.Json;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class ApiMetadataTests
{
    [Fact]
    public async Task HttpClose_UpdatesApiDocsStatusToClosed()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-001", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
    }

    [Fact]
    public async Task HttpClose_RecordsDocCloseEvent()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-002", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recorded = apiStore.GetEvent("evt-close-meta-002");
        Assert.NotNull(recorded);
        Assert.Equal("DOC_CLOSE", recorded!.EventType);
        Assert.Equal(docUid, recorded.DocUid);
        Assert.Equal(1, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    [Fact]
    public async Task Replay_ReconcilesMetadataWithoutRepostingLedger()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-003", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);

        var firstClosedAt = harness.GetDoc(1).ClosedAt;
        apiStore.UpdateApiDocStatus(docUid, "DRAFT");

        using var second = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-004", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var payload = await second.Content.ReadFromJsonAsync<CloseDocResponse>();
        Assert.NotNull(payload);
        Assert.True(payload!.Ok);
        Assert.True(payload.Closed);
        Assert.Equal("ALREADY_CLOSED", payload.Result);
        Assert.True(payload.AlreadyClosed);
        Assert.False(payload.IdempotentReplay);
        Assert.Empty(payload.Errors);

        Assert.Single(harness.LedgerEntries);
        Assert.Equal(firstClosedAt, harness.GetDoc(1).ClosedAt);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(2, apiStore.CountEvents("DOC_CLOSE", docUid));
        Assert.NotNull(apiStore.GetEvent("evt-close-meta-004"));
    }
}
