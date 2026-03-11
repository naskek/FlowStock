using System.Net;
using System.Net.Http.Json;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class ValidationTests
{
    [Fact]
    public async Task MissingDocUid_Fails()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                EventId = "evt-create-val-001",
                Type = "INBOUND",
                DraftOnly = true
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("MISSING_DOC_UID", payload.Error);
        Assert.Equal(0, harness.DocCount);
        Assert.Equal(0, apiStore.ApiDocCount);
    }

    [Fact]
    public async Task MissingEventId_Fails()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                DocUid = "draft-val-002",
                Type = "INBOUND",
                DraftOnly = true
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("MISSING_EVENT_ID", payload.Error);
        Assert.Equal(0, harness.DocCount);
        Assert.Equal(0, apiStore.ApiDocCount);
    }

    [Fact]
    public async Task InvalidType_Fails()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                DocUid = "draft-val-003",
                EventId = "evt-create-val-003",
                Type = "UNKNOWN",
                DraftOnly = true
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("INVALID_TYPE", payload.Error);
        Assert.Equal(0, harness.DocCount);
        Assert.Equal(0, apiStore.ApiDocCount);
    }

    [Fact]
    public async Task DraftOnlyFalse_MissingPartner_FailsForInbound()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                DocUid = "draft-val-004",
                EventId = "evt-create-val-004",
                Type = "INBOUND",
                ToLocationId = 10,
                DraftOnly = false
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("MISSING_PARTNER", payload.Error);
        Assert.Equal(0, harness.DocCount);
        Assert.Equal(0, apiStore.ApiDocCount);
    }

    [Fact]
    public async Task UnknownPartner_Fails()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs",
            new CreateDocRequest
            {
                DocUid = "draft-val-005",
                EventId = "evt-create-val-005",
                Type = "INBOUND",
                PartnerId = 999,
                ToLocationId = 10,
                DraftOnly = false
            });

        var payload = await CreateDocDraftHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("UNKNOWN_PARTNER", payload.Error);
        Assert.Equal(0, harness.DocCount);
        Assert.Equal(0, apiStore.ApiDocCount);
    }
}
