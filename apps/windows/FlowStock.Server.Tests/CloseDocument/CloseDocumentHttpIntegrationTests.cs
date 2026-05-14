using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;

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

    [Fact]
    public async Task HttpClose_InternalProductionReceiptWithMarkableItemWithoutKmCodes_IsRejected()
    {
        var (harness, apiStore, docUid) = CreateInternalProductionReceiptScenario(markable: true, kmCodes: 0);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-prd-marking-001", DeviceId = "WPF-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await ReadCloseResponseAsync(response);
        Assert.False(payload.Ok);
        Assert.False(payload.Closed);
        Assert.Equal("VALIDATION_FAILED", payload.Result);
        Assert.Contains("Строка 1 (Маркируемый товар): требуется 5 код(ов) КМ, привязано 0, доступно свободных 0.", payload.Errors);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(1).Status);
        Assert.Equal("DRAFT", apiStore.GetApiDoc(docUid)?.Status);
    }

    [Fact]
    public async Task HttpClose_InternalProductionReceiptWithMarkableItemWithEnoughKmCodes_Closes()
    {
        var (harness, apiStore, docUid) = CreateInternalProductionReceiptScenario(markable: true, kmCodes: 5);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-prd-marking-002", DeviceId = "WPF-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await ReadCloseResponseAsync(response);
        Assert.True(payload.Ok);
        Assert.True(payload.Closed);
        Assert.Equal("CLOSED", payload.Result);
        Assert.Empty(payload.Errors);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(1).Status);
    }

    [Fact]
    public async Task HttpClose_InternalProductionReceiptWithNonMarkableItemWithoutKmCodes_Closes()
    {
        var (harness, apiStore, docUid) = CreateInternalProductionReceiptScenario(markable: false, kmCodes: 0);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-prd-marking-003", DeviceId = "WPF-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await ReadCloseResponseAsync(response);
        Assert.True(payload.Ok);
        Assert.True(payload.Closed);
        Assert.Equal("CLOSED", payload.Result);
        Assert.Empty(payload.Errors);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(1).Status);
    }

    [Fact]
    public async Task TsdOutbound_OrderBoundHu_ClosesShipmentAndWritesLedger()
    {
        const string docUid = "tsd-outbound-order-bound-001";
        var harness = CreateTsdOutboundOrderBoundScenario();
        var apiStore = new InMemoryApiDocStore();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var created = await CreateDocDraftHttpApi.CreateAsync(
            host.Client,
            new CreateDocRequest
            {
                DocUid = docUid,
                EventId = "evt-tsd-outbound-create-001",
                DeviceId = "TSD-01",
                Type = "OUTBOUND",
                OrderId = 20,
                FromLocationId = 1,
                FromHu = "HU-CUST-001",
                DraftOnly = true
            });
        Assert.True(created.Ok);

        var line = await CreateDocLineHttpApi.AddAsync(
            host.Client,
            docUid,
            new AddDocLineRequest
            {
                EventId = "evt-tsd-outbound-line-001",
                DeviceId = "TSD-01",
                ItemId = 1001,
                OrderLineId = 201,
                Qty = 5,
                FromLocationId = 1,
                FromHu = "HU-CUST-001"
            });
        Assert.True(line.Ok);
        Assert.Equal(201, line.Line?.OrderLineId);
        Assert.Equal("HU-CUST-001", line.Line?.FromHu);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-tsd-outbound-close-001", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ReadCloseResponseAsync(response);
        Assert.True(payload.Ok);
        Assert.True(payload.Closed);
        Assert.Empty(payload.Errors);

        var ledger = Assert.Single(harness.LedgerEntries);
        Assert.Equal(1001, ledger.ItemId);
        Assert.Equal(1, ledger.LocationId);
        Assert.Equal("HU-CUST-001", ledger.HuCode);
        Assert.Equal(-5, ledger.QtyDelta);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    private static async Task<CloseDocResponse> ReadCloseResponseAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<CloseDocResponse>();
        return Assert.IsType<CloseDocResponse>(payload);
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocUid) CreateInternalProductionReceiptScenario(
        bool markable,
        int kmCodes)
    {
        const string docUid = "doc-http-prd-2026-000001";

        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000010",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "INT-2026-000010",
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = markable ? "Маркируемый товар" : "Обычный товар",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = markable
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "01",
            Name = "Склад 01"
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "INT-2026-000010",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 7, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 1000,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedOrderReceiptRemaining(10, new OrderReceiptLine
        {
            OrderLineId = 1000,
            OrderId = 10,
            ItemId = 100,
            ItemName = markable ? "Маркируемый товар" : "Обычный товар",
            QtyOrdered = 5,
            QtyReceived = 0,
            QtyRemaining = 5,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            OrderLineId = 1000,
            ItemId = 100,
            Qty = 5,
            ToLocationId = 10,
            ToHu = "HU-PRD-001"
        });
        harness.SeedKmCodeCountByReceiptLine(docLineId: 11, count: kmCodes);

        var apiStore = new InMemoryApiDocStore();
        apiStore.AddApiDoc(
            docUid,
            docId: 1,
            status: "DRAFT",
            docType: "PRODUCTION_RECEIPT",
            docRef: "PRD-2026-000010",
            partnerId: null,
            fromLocationId: null,
            toLocationId: 10,
            fromHu: null,
            toHu: null,
            deviceId: "WPF-01");

        return (harness, apiStore, docUid);
    }

    private static CloseDocumentHarness CreateTsdOutboundOrderBoundScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 8, 8, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-020",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedDoc(new Doc
        {
            Id = 3,
            DocRef = "PRD-2026-000003",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 20,
            OrderRef = "SO-020",
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 31,
            DocId = 3,
            OrderLineId = 201,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-CUST-001"
        });
        harness.SeedHu(new HuRecord
        {
            Id = 1,
            Code = "HU-CUST-001",
            Status = "ACTIVE",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 5, huCode: "HU-CUST-001");
        return harness;
    }
}
