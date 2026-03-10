using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class TsdCompatibilityTests
{
    [Fact]
    public async Task CreateLinesClose_ResultsInClosed()
    {
        const string docUid = "tsd-doc-001";
        const string docRef = "IN-TSD-001";

        var (harness, apiStore) = CloseDocumentHttpScenario.CreateTsdInboundFlowScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDraftAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-create-001",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = docRef,
                ToLocationId = 10,
                DraftOnly = true
            });

        var addedLine = await AddLineAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-line-001",
                DeviceId = "TSD-01",
                Barcode = "4660011933641",
                Qty = 12
            });

        using var closeResponse = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest
            {
                EventId = "evt-tsd-close-001",
                DeviceId = "TSD-01"
            });

        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);

        Assert.True(created.Ok);
        Assert.NotNull(created.Doc);
        Assert.Equal(docUid, created.Doc!.DocUid);
        Assert.Equal(docRef, created.Doc.DocRef);
        Assert.Equal("DRAFT", created.Doc.Status);
        Assert.Equal("INBOUND", created.Doc.Type);
        Assert.False(created.Doc.DocRefChanged);

        Assert.True(addedLine.Ok);
        Assert.NotNull(addedLine.Line);
        Assert.Equal(100, addedLine.Line!.ItemId);
        Assert.Equal(12, addedLine.Line.Qty);

        var payload = await ReadCloseResponseAsync(closeResponse);
        Assert.True(payload.Ok);
        Assert.True(payload.Closed);
        Assert.Equal(docUid, payload.DocUid);
        Assert.Equal(docRef, payload.DocRef);
        Assert.Equal("CLOSED", payload.DocStatus);
        Assert.Equal("CLOSED", payload.Result);
        Assert.False(payload.IdempotentReplay);
        Assert.False(payload.AlreadyClosed);
        Assert.Empty(payload.Errors);
        Assert.Empty(payload.Warnings);

        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid));
        var doc = harness.GetDoc(docInfo.DocId);
        Assert.Equal(DocType.Inbound, doc.Type);
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.NotNull(doc.ClosedAt);

        var entry = Assert.Single(harness.LedgerEntries);
        Assert.Equal(doc.Id, entry.DocId);
        Assert.Equal(100, entry.ItemId);
        Assert.Equal(10, entry.LocationId);
        Assert.Equal(12, entry.QtyDelta);

        Assert.Equal("CLOSED", docInfo.Status);
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", docUid));
        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", docUid));
        Assert.Equal(1, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    [Fact]
    public async Task CreateLinesWithoutClose_RemainsDraft()
    {
        const string docUid = "tsd-doc-002";
        const string docRef = "IN-TSD-002";

        var (harness, apiStore) = CloseDocumentHttpScenario.CreateTsdInboundFlowScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDraftAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-create-002",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = docRef,
                ToLocationId = 10,
                DraftOnly = true
            });

        await AddLineAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-line-002",
                DeviceId = "TSD-01",
                Barcode = "4660011933641",
                Qty = 7
            });

        var docInfo = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid));
        var doc = harness.GetDoc(docInfo.DocId);

        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal("DRAFT", docInfo.Status);
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", docUid));
        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", docUid));
        Assert.Equal(0, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    [Fact]
    public async Task RepeatedClose_IsIdempotent()
    {
        const string docUid = "tsd-doc-003";

        var (harness, apiStore) = CloseDocumentHttpScenario.CreateTsdInboundFlowScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDraftAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-create-003",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-TSD-003",
                ToLocationId = 10,
                DraftOnly = true
            });

        await AddLineAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-line-003",
                DeviceId = "TSD-01",
                Barcode = "4660011933641",
                Qty = 9
            });

        using var first = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest
            {
                EventId = "evt-tsd-close-003",
                DeviceId = "TSD-01"
            });
        var docId = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid)).DocId;
        var firstClosedAt = harness.GetDoc(docId).ClosedAt;

        using var second = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest
            {
                EventId = "evt-tsd-close-003",
                DeviceId = "TSD-01"
            });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondPayload = await ReadCloseResponseAsync(second);
        Assert.True(secondPayload.Ok);
        Assert.True(secondPayload.Closed);
        Assert.Equal("CLOSED", secondPayload.DocStatus);
        Assert.Equal("CLOSED", secondPayload.Result);
        Assert.True(secondPayload.IdempotentReplay);
        Assert.False(secondPayload.AlreadyClosed);
        Assert.Empty(secondPayload.Errors);
        Assert.Empty(secondPayload.Warnings);

        Assert.Single(harness.LedgerEntries);
        Assert.Equal(firstClosedAt, harness.GetDoc(docId).ClosedAt);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(1, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    [Fact]
    public async Task AlreadyClosedAfterFullFlow_ReturnsCanonicalNoOp()
    {
        const string docUid = "tsd-doc-004";

        var (harness, apiStore) = CloseDocumentHttpScenario.CreateTsdInboundFlowScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDraftAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-create-004",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                DocRef = "IN-TSD-004",
                ToLocationId = 10,
                DraftOnly = true
            });

        await AddLineAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-line-004",
                DeviceId = "TSD-01",
                Barcode = "4660011933641",
                Qty = 3
            });

        using var first = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest
            {
                EventId = "evt-tsd-close-004",
                DeviceId = "TSD-01"
            });
        var docId = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid)).DocId;
        var firstClosedAt = harness.GetDoc(docId).ClosedAt;

        using var second = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest
            {
                EventId = "evt-tsd-close-005",
                DeviceId = "TSD-01"
            });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondPayload = await ReadCloseResponseAsync(second);
        Assert.True(secondPayload.Ok);
        Assert.True(secondPayload.Closed);
        Assert.Equal("CLOSED", secondPayload.DocStatus);
        Assert.Equal("ALREADY_CLOSED", secondPayload.Result);
        Assert.False(secondPayload.IdempotentReplay);
        Assert.True(secondPayload.AlreadyClosed);
        Assert.Empty(secondPayload.Errors);
        Assert.Empty(secondPayload.Warnings);

        Assert.Single(harness.LedgerEntries);
        Assert.Equal(firstClosedAt, harness.GetDoc(docId).ClosedAt);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(2, apiStore.CountEvents("DOC_CLOSE", docUid));
        Assert.NotNull(apiStore.GetEvent("evt-tsd-close-005"));
    }

    [Fact]
    public async Task WriteOffWithoutReason_FailsThroughRealEndpointLifecycle()
    {
        const string docUid = "tsd-doc-005";
        const string docRef = "WO-TSD-001";

        var (harness, apiStore) = CloseDocumentHttpScenario.CreateTsdWriteOffFlowScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDraftAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-create-005",
                DeviceId = "TSD-01",
                Type = "WRITE_OFF",
                DocRef = docRef,
                FromLocationId = 10,
                DraftOnly = true
            });

        await AddLineAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-line-005",
                DeviceId = "TSD-01",
                Barcode = "4660011933641",
                Qty = 5
            });

        using var closeResponse = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest
            {
                EventId = "evt-tsd-close-006",
                DeviceId = "TSD-01"
            });

        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);

        var payload = await ReadCloseResponseAsync(closeResponse);
        Assert.False(payload.Ok);
        Assert.False(payload.Closed);
        Assert.Equal(docUid, payload.DocUid);
        Assert.Equal(docRef, payload.DocRef);
        Assert.Equal("DRAFT", payload.DocStatus);
        Assert.Equal("VALIDATION_FAILED", payload.Result);
        Assert.Contains(payload.Errors, error => error.Contains("требуется причина", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(payload.Warnings);
        Assert.False(payload.IdempotentReplay);
        Assert.False(payload.AlreadyClosed);

        var docId = Assert.IsType<ApiDocInfo>(apiStore.GetApiDoc(docUid)).DocId;
        var doc = harness.GetDoc(docId);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal("DRAFT", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(1, apiStore.CountEvents("DOC_CREATE", docUid));
        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", docUid));
        Assert.Equal(0, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    private static async Task<CreateDocEnvelope> CreateDraftAsync(HttpClient client, CreateDocRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/docs", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<CreateDocEnvelope>();
        return Assert.IsType<CreateDocEnvelope>(payload);
    }

    private static async Task<AddLineEnvelope> AddLineAsync(HttpClient client, string docUid, AddDocLineRequest request)
    {
        using var response = await client.PostAsJsonAsync($"/api/docs/{docUid}/lines", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<AddLineEnvelope>();
        return Assert.IsType<AddLineEnvelope>(payload);
    }

    private static async Task<CloseDocResponse> ReadCloseResponseAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<CloseDocResponse>();
        return Assert.IsType<CloseDocResponse>(payload);
    }

    private sealed class CreateDocEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("doc")]
        public CreateDocPayload? Doc { get; init; }
    }

    private sealed class CreateDocPayload
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("doc_uid")]
        public string? DocUid { get; init; }

        [JsonPropertyName("doc_ref")]
        public string? DocRef { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("doc_ref_changed")]
        public bool DocRefChanged { get; init; }
    }

    private sealed class AddLineEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("line")]
        public AddLinePayload? Line { get; init; }
    }

    private sealed class AddLinePayload
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("uom_code")]
        public string? UomCode { get; init; }
    }
}
