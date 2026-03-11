using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class MetadataTests
{
    [Fact]
    public async Task AddLine_WritesDocLineEventToApiEvents()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-meta-001", "evt-line-meta-create-001");

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            "line-meta-001",
            new AddDocLineRequest
            {
                EventId = "evt-line-meta-001",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 4
            });

        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", "line-meta-001"));
        Assert.Empty(harness.LedgerEntries);
    }
}
