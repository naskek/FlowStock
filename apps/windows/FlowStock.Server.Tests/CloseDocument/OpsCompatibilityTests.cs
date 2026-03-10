using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class OpsCompatibilityTests
{
    [Fact]
    public async Task ApiOps_ConvergesToCanonicalCloseSemantics()
    {
        var (harness, apiStore, docRef) = CloseDocumentHttpScenario.CreateOpsReceiveDraftlessScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/ops",
            CreateReceiveRequest("evt-op-001", docRef));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<CloseDocResponse>();
        Assert.NotNull(payload);
        Assert.True(payload!.Ok);
        Assert.True(payload.Closed);
        Assert.Null(payload.DocUid);
        Assert.Equal(docRef, payload.DocRef);
        Assert.Equal("CLOSED", payload.DocStatus);
        Assert.Equal("CLOSED", payload.Result);
        Assert.False(payload.IdempotentReplay);
        Assert.False(payload.AlreadyClosed);
        Assert.Empty(payload.Errors);
        Assert.Empty(payload.Warnings);

        var doc = Assert.IsType<Doc>(harness.FindDocByRef(docRef));
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.NotNull(doc.ClosedAt);
        Assert.Equal(DocType.Inbound, doc.Type);

        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal(doc.Id, entry.DocId);
        Assert.Equal(100, entry.ItemId);
        Assert.Equal(10, entry.LocationId);
        Assert.Equal(10, entry.QtyDelta);

        var opEvent = apiStore.GetEvent("evt-op-001");
        Assert.NotNull(opEvent);
        Assert.Equal("OP", opEvent!.EventType);
        Assert.Equal(1, apiStore.CountEvents("OP"));
    }

    [Fact]
    public async Task ApiOps_Replay_DoesNotDuplicateLedger()
    {
        var (harness, apiStore, docRef) = CloseDocumentHttpScenario.CreateOpsReceiveDraftlessScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsJsonAsync(
            "/api/ops",
            CreateReceiveRequest("evt-op-002", docRef));
        var firstClosedAt = harness.FindDocByRef(docRef)?.ClosedAt;

        using var second = await host.Client.PostAsJsonAsync(
            "/api/ops",
            CreateReceiveRequest("evt-op-002", docRef));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondPayload = await second.Content.ReadFromJsonAsync<CloseDocResponse>();
        Assert.NotNull(secondPayload);
        Assert.True(secondPayload!.Ok);
        Assert.True(secondPayload.Closed);
        Assert.Equal(docRef, secondPayload.DocRef);
        Assert.Equal("CLOSED", secondPayload.DocStatus);
        Assert.Equal("CLOSED", secondPayload.Result);
        Assert.True(secondPayload.IdempotentReplay);
        Assert.False(secondPayload.AlreadyClosed);
        Assert.Empty(secondPayload.Errors);

        Assert.Single(harness.LedgerEntries);
        Assert.Equal(firstClosedAt, harness.FindDocByRef(docRef)?.ClosedAt);
        Assert.Equal(1, apiStore.CountEvents("OP"));
    }

    [Fact]
    public async Task ApiOps_AlreadyClosed_NewEventId_ReturnsCanonicalNoOp()
    {
        var (harness, apiStore, docRef) = CloseDocumentHttpScenario.CreateOpsReceiveDraftlessScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsJsonAsync(
            "/api/ops",
            CreateReceiveRequest("evt-op-003", docRef));
        var firstClosedAt = harness.FindDocByRef(docRef)?.ClosedAt;

        using var second = await host.Client.PostAsJsonAsync(
            "/api/ops",
            CreateReceiveRequest("evt-op-004", docRef));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondPayload = await second.Content.ReadFromJsonAsync<CloseDocResponse>();
        Assert.NotNull(secondPayload);
        Assert.True(secondPayload!.Ok);
        Assert.True(secondPayload.Closed);
        Assert.Equal(docRef, secondPayload.DocRef);
        Assert.Equal("CLOSED", secondPayload.DocStatus);
        Assert.Equal("ALREADY_CLOSED", secondPayload.Result);
        Assert.False(secondPayload.IdempotentReplay);
        Assert.True(secondPayload.AlreadyClosed);
        Assert.Empty(secondPayload.Errors);
        Assert.Empty(secondPayload.Warnings);

        Assert.Single(harness.LedgerEntries);
        Assert.Equal(firstClosedAt, harness.FindDocByRef(docRef)?.ClosedAt);
        Assert.Equal(2, apiStore.CountEvents("OP"));
        Assert.NotNull(apiStore.GetEvent("evt-op-004"));
    }

    private static OperationEventRequest CreateReceiveRequest(string eventId, string docRef)
    {
        return new OperationEventRequest
        {
            SchemaVersion = 1,
            EventId = eventId,
            Timestamp = "2026-03-10T12:00:00Z",
            DeviceId = "CT48-01",
            Op = "RECEIVE",
            DocRef = docRef,
            Barcode = "4660011933641",
            Qty = 10,
            ToLoc = "A1"
        };
    }
}
