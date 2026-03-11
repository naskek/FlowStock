using System.Net;
using System.Net.Http.Json;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task SameEventIdReplay_DoesNotDuplicateDocsApiDocsOrEvents()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var request = new CreateDocRequest
        {
            DocUid = "draft-idem-001",
            EventId = "evt-create-idem-001",
            DeviceId = "API-01",
            Type = "INBOUND",
            DocRef = "IN-IDEM-001",
            DraftOnly = true
        };

        var first = await CreateDocDraftHttpApi.CreateAsync(host.Client, request);
        var second = await CreateDocDraftHttpApi.CreateAsync(host.Client, request);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotNull(first.Doc);
        Assert.NotNull(second.Doc);
        Assert.Equal(first.Doc!.Id, second.Doc!.Id);
        Assert.Equal("draft-idem-001", second.Doc.DocUid);
        Assert.Equal("IN-IDEM-001", second.Doc.DocRef);
        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", "draft-idem-001"));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task SameEventIdWithConflictingPayload_ReturnsEventConflict()
    {
        const string docUid = "draft-idem-002";
        const string eventId = "evt-create-idem-002";

        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = eventId,
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = "IN-IDEM-002",
                DraftOnly = true
            });

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = eventId,
                DeviceId = "API-01",
                Type = "INBOUND",
                DocRef = "IN-IDEM-002-CHANGED",
                DraftOnly = true
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("EVENT_ID_CONFLICT", payload.Error);
        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", docUid));
        Assert.Empty(harness.LedgerEntries);
    }
}
