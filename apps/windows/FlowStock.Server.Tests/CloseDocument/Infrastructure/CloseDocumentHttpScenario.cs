using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.CloseDocument.Infrastructure;

internal static class CloseDocumentHttpScenario
{
    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocUid) CreateInboundDraft()
    {
        const string docUid = "doc-http-2026-000001";

        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "IN-2026-000010",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Горчица"
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "01",
            Name = "Склад 01"
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            ItemId = 100,
            Qty = 12,
            ToLocationId = 10
        });

        var apiStore = new InMemoryApiDocStore();
        apiStore.AddApiDoc(
            docUid,
            docId: 1,
            status: "DRAFT",
            docType: "INBOUND",
            docRef: "IN-2026-000010",
            partnerId: null,
            fromLocationId: null,
            toLocationId: 10,
            fromHu: null,
            toHu: null,
            deviceId: "TSD-01");

        return (harness, apiStore, docUid);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocRef) CreateOpsReceiveDraftlessScenario()
    {
        const string docRef = "OPS-RECEIVE-001";

        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Горчица",
            Barcode = "4660011933641"
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "A1",
            Name = "Зона A1"
        });

        var apiStore = new InMemoryApiDocStore();
        return (harness, apiStore, docRef);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateTsdInboundFlowScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Горчица",
            Barcode = "4660011933641"
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "A1",
            Name = "Зона A1"
        });

        var apiStore = new InMemoryApiDocStore();
        return (harness, apiStore);
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateTsdWriteOffFlowScenario()
    {
        var (harness, apiStore) = CreateTsdInboundFlowScenario();
        harness.SeedBalance(itemId: 100, locationId: 10, qty: 50);
        return (harness, apiStore);
    }
}
