using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class AppendOnlyBehaviorTests
{
    [Fact]
    public async Task TwoDifferentEventIdsForSameSemanticLine_CreateTwoRows()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-append-001", "evt-line-append-create-001");

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-append-001",
            new AddDocLineRequest
            {
                EventId = "evt-line-append-001",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5,
                UomCode = "BOX"
            });

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-append-001",
            new AddDocLineRequest
            {
                EventId = "evt-line-append-002",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5,
                UomCode = "BOX"
            });

        var lines = harness.GetDocLines(created.Doc!.Id);
        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].Id, lines[1].Id);
        Assert.All(lines, line =>
        {
            Assert.Equal(100, line.ItemId);
            Assert.Equal(5, line.Qty);
            Assert.Equal(10, line.ToLocationId);
        });
        Assert.Equal(2, apiStore.CountEvents("DOC_LINE", "line-append-001"));
        Assert.Empty(harness.LedgerEntries);
    }
}
