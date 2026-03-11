using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task SameEventIdSamePayload_ReturnsIdempotentReplay()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-idem-001", "evt-line-idem-create-001");

        var request = new AddDocLineRequest
        {
            EventId = "evt-line-idem-001",
            DeviceId = "API-01",
            ItemId = 100,
            Qty = 5,
            UomCode = "BOX"
        };

        var first = await CreateDocLineHttpApi.AddAsync(host.Client, "line-idem-001", request);
        var second = await CreateDocLineHttpApi.AddAsync(host.Client, "line-idem-001", request);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal("APPENDED", first.Result);
        Assert.Equal("IDEMPOTENT_REPLAY", second.Result);
        Assert.False(first.IdempotentReplay);
        Assert.True(second.IdempotentReplay);
        Assert.False(second.Appended);
        Assert.Equal("DRAFT", second.DocStatus);
        Assert.NotNull(first.Line);
        Assert.NotNull(second.Line);
        Assert.Equal(first.Line!.Id, second.Line!.Id);
        var doc = harness.GetDoc(created.Doc!.Id);
        Assert.Single(harness.GetDocLines(created.Doc.Id));
        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", "line-idem-001"));
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task SameEventIdDifferentPayload_ReturnsEventIdConflict()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-idem-002", "evt-line-idem-create-002");

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-idem-002",
            new AddDocLineRequest
            {
                EventId = "evt-line-idem-002",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5
            });

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs/line-idem-002/lines",
            new AddDocLineRequest
            {
                EventId = "evt-line-idem-002",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 6
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("EVENT_ID_CONFLICT", payload.Error);
        var doc = harness.GetDoc(created.Doc!.Id);
        Assert.Single(harness.GetDocLines(created.Doc.Id));
        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", "line-idem-002"));
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Empty(harness.LedgerEntries);
    }
}
