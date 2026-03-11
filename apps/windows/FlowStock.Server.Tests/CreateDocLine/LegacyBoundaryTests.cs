using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class LegacyBoundaryTests
{
    [Fact]
    public async Task ApiOps_RemainsOutsideCanonicalLifecycle()
    {
        var (harness, apiStore, docRef) = CloseDocumentHttpScenario.CreateOpsReceiveDraftlessScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/ops",
            new OperationEventRequest
            {
                SchemaVersion = 1,
                EventId = "evt-op-line-001",
                Timestamp = "2026-03-10T12:00:00Z",
                DeviceId = "CT48-01",
                Op = "RECEIVE",
                DocRef = docRef,
                Barcode = "4660011933641",
                Qty = 10,
                ToLoc = "A1"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<CloseDocResponse>();
        var doc = Assert.IsType<Doc>(harness.FindDocByRef(docRef));

        Assert.NotNull(payload);
        Assert.True(payload!.Ok);
        Assert.True(payload.Closed);
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.Equal(0, apiStore.ApiDocCount);
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE"));
        Assert.Equal(1, apiStore.CountEvents("OP"));
    }

    [Fact(Skip = "JSONL import still runs through local ImportService/imported_events and there is no dedicated import harness in FlowStock.Server.Tests without widening test infrastructure.")]
    public void JsonlImport_RemainsOutsideCanonicalLifecycle()
    {
    }
}
