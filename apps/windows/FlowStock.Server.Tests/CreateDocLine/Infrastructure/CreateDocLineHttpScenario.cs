using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine.Infrastructure;

internal static class CreateDocLineHttpScenario
{
    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateEmptyScenario()
    {
        return (new CloseDocumentHarness(), new InMemoryApiDocStore());
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateInboundScenario()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Mustard",
            Barcode = "4660011933641"
        });

        return (harness, apiStore);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocUid) CreateClosedInboundScenario()
    {
        const string docUid = "line-closed-001";

        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Mustard",
            Barcode = "4660011933641"
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "A1",
            Name = "Zone A1"
        });
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "IN-LINE-CLOSED-001",
            Type = DocType.Inbound,
            Status = DocStatus.Closed,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 3, 10, 11, 0, 0, DateTimeKind.Utc)
        });

        var apiStore = new InMemoryApiDocStore();
        apiStore.AddApiDoc(
            docUid,
            docId: 1,
            status: "CLOSED",
            docType: "INBOUND",
            docRef: "IN-LINE-CLOSED-001",
            partnerId: null,
            fromLocationId: null,
            toLocationId: 10,
            fromHu: null,
            toHu: null,
            deviceId: "API-01");

        return (harness, apiStore, docUid);
    }

    public static async Task<CreateDocDraftHttpApi.CreateDocEnvelope> CreateInboundDraftAsync(HttpClient client, string docUid, string eventId)
    {
        return await CreateDocDraftHttpApi.CreateAsync(
            client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = eventId,
                DeviceId = "API-01",
                Type = "INBOUND",
                ToLocationId = 10,
                DraftOnly = true
            });
    }
}
