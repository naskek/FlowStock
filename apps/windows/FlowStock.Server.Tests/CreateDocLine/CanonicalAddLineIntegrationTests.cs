using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class CanonicalAddLineIntegrationTests
{
    [Fact]
    public async Task FirstAddLine_CreatesOneDocLinesRow()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-can-001", "evt-line-can-create-001");
        var docId = created.Doc!.Id;

        var payload = await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-can-001",
            new AddDocLineRequest
            {
                EventId = "evt-line-can-001",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5,
                UomCode = "BOX"
            });

        Assert.True(payload.Ok);
        Assert.NotNull(payload.Line);
        Assert.Equal(100, payload.Line!.ItemId);
        Assert.Equal(5, payload.Line.Qty);
        Assert.Equal("BOX", payload.Line.UomCode);

        var line = Assert.Single(harness.GetDocLines(docId));
        Assert.Equal(payload.Line.Id, line.Id);
        Assert.Equal(100, line.ItemId);
        Assert.Equal(5, line.Qty);
        Assert.Equal(10, line.ToLocationId);
    }

    [Fact]
    public async Task AddLine_DoesNotWriteLedger()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-can-002", "evt-line-can-create-002");

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-can-002",
            new AddDocLineRequest
            {
                EventId = "evt-line-can-002",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 3
            });

        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task AddLine_DoesNotChangeDocsStatus()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-can-003", "evt-line-can-create-003");
        var docId = created.Doc!.Id;

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-can-003",
            new AddDocLineRequest
            {
                EventId = "evt-line-can-003",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 7
            });

        var doc = harness.GetDoc(docId);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
    }
}
