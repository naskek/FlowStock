using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class TsdCompatibilityTests
{
    [Fact]
    public async Task TsdCreateThenAddLines_KeepsServerDocInDraft()
    {
        const string docUid = "tsd-line-001";

        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-line-create-001",
                DeviceId = "TSD-01",
                Type = "INBOUND",
                Comment = "TSD",
                ToLocationId = 10,
                DraftOnly = true
            });

        await CreateDocLineHttpApi.AddAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-line-001",
                DeviceId = "TSD-01",
                Barcode = "4660011933641",
                Qty = 8
            });

        var doc = harness.GetDoc(created.Doc!.Id);
        Assert.Equal(DocStatus.Draft, doc.Status);
        Assert.Null(doc.ClosedAt);
        Assert.Single(harness.GetDocLines(created.Doc.Id));
        Assert.Equal(1, apiStore.CountEvents("DOC_LINE", docUid));
        Assert.Empty(harness.LedgerEntries);
    }
}
