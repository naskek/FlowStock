using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class CloseDocumentHttpIntegrationTests
{
    [Fact]
    public async Task HttpClose_SuccessfulClose_UpdatesAuthoritativeState()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-001", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await ReadCloseResponseAsync(response);
        Assert.True(payload.Ok);
        Assert.True(payload.Closed);
        Assert.Equal(docUid, payload.DocUid);
        Assert.Equal("IN-2026-000010", payload.DocRef);
        Assert.Equal("CLOSED", payload.DocStatus);
        Assert.Equal("CLOSED", payload.Result);
        Assert.False(payload.IdempotentReplay);
        Assert.False(payload.AlreadyClosed);
        Assert.Empty(payload.Errors);
        Assert.Empty(payload.Warnings);

        var doc = harness.GetDoc(1);
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.NotNull(doc.ClosedAt);

        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal(1, entry.DocId);
        Assert.Equal(100, entry.ItemId);
        Assert.Equal(10, entry.LocationId);
        Assert.Equal(12, entry.QtyDelta);
    }

    [Fact]
    public async Task HttpClose_RepeatedSameEvent_DoesNotDuplicateLedger()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-002", DeviceId = "TSD-01" });
        using var second = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-002", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstPayload = await ReadCloseResponseAsync(first);
        var firstClosedAt = harness.GetDoc(1).ClosedAt;
        var secondPayload = await ReadCloseResponseAsync(second);

        Assert.True(firstPayload.Ok);
        Assert.True(firstPayload.Closed);
        Assert.Equal("CLOSED", firstPayload.Result);
        Assert.False(firstPayload.IdempotentReplay);
        Assert.False(firstPayload.AlreadyClosed);

        Assert.True(secondPayload.Ok);
        Assert.True(secondPayload.Closed);
        Assert.Equal(docUid, secondPayload.DocUid);
        Assert.Equal("IN-2026-000010", secondPayload.DocRef);
        Assert.Equal("CLOSED", secondPayload.DocStatus);
        Assert.Equal("CLOSED", secondPayload.Result);
        Assert.True(secondPayload.IdempotentReplay);
        Assert.False(secondPayload.AlreadyClosed);
        Assert.Empty(secondPayload.Errors);
        Assert.Empty(secondPayload.Warnings);

        Assert.Single(harness.LedgerEntries);

        var doc = harness.GetDoc(1);
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.Equal(firstClosedAt, doc.ClosedAt);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(1, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    private static async Task<CloseDocResponse> ReadCloseResponseAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<CloseDocResponse>();
        return Assert.IsType<CloseDocResponse>(payload);
    }
}
