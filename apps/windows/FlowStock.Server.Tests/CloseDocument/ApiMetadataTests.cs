using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class ApiMetadataTests
{
    [Fact]
    public async Task HttpClose_UpdatesApiDocsStatusToClosed()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-001", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
    }

    [Fact]
    public async Task HttpClose_RecordsDocCloseEvent()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-002", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recorded = apiStore.GetEvent("evt-close-meta-002");
        Assert.NotNull(recorded);
        Assert.Equal("DOC_CLOSE", recorded!.EventType);
        Assert.Equal(docUid, recorded.DocUid);
        Assert.Equal(1, apiStore.CountEvents("DOC_CLOSE", docUid));
    }

    [Fact]
    public async Task Replay_ReconcilesMetadataWithoutRepostingLedger()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-003", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);

        var firstClosedAt = harness.GetDoc(1).ClosedAt;
        apiStore.UpdateApiDocStatus(docUid, "DRAFT");

        using var second = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-meta-004", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var payload = await second.Content.ReadFromJsonAsync<CloseDocResponse>();
        Assert.NotNull(payload);
        Assert.True(payload!.Ok);
        Assert.True(payload.Closed);
        Assert.Equal("ALREADY_CLOSED", payload.Result);
        Assert.True(payload.AlreadyClosed);
        Assert.False(payload.IdempotentReplay);
        Assert.Empty(payload.Errors);

        Assert.Single(harness.LedgerEntries);
        Assert.Equal(firstClosedAt, harness.GetDoc(1).ClosedAt);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(2, apiStore.CountEvents("DOC_CLOSE", docUid));
        Assert.NotNull(apiStore.GetEvent("evt-close-meta-004"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task OutboundCloseLog_ReportsOnlyActualPermissionReset(
        bool permissionBefore,
        bool expectedReset)
    {
        var (harness, apiStore, docUid) = CreateOutboundDraft(permissionBefore);
        using var logs = new CapturingLoggerProvider();
        await using var host = await CloseDocumentHttpHost.StartAsync(
            harness,
            apiStore,
            logging => logging.AddProvider(logs));

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = $"evt-outbound-{permissionBefore}", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = (await response.Content.ReadFromJsonAsync<CloseDocResponse>())!;
        Assert.True(payload.Ok, string.Join(" | ", payload.Errors));
        var entry = Assert.Single(DocumentCloseEntries(logs));
        Assert.Equal(expectedReset, entry.Properties["PartialOutboundPermissionAutoReset"]);
        Assert.Equal(false, entry.Properties["AllowPartialOutboundAfter"]);
    }

    [Fact]
    public async Task OutboundCloseLog_IdempotentReplayDoesNotReportPermissionReset()
    {
        var (harness, apiStore, docUid) = CreateOutboundDraft(permissionBefore: true);
        using var logs = new CapturingLoggerProvider();
        await using var host = await CloseDocumentHttpHost.StartAsync(
            harness,
            apiStore,
            logging => logging.AddProvider(logs));
        var request = new CloseDocRequest { EventId = "evt-outbound-replay", DeviceId = "TSD-01" };

        using var first = await host.Client.PostAsJsonAsync($"/api/docs/{docUid}/close", request);
        using var replay = await host.Client.PostAsJsonAsync($"/api/docs/{docUid}/close", request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var entries = DocumentCloseEntries(logs);
        Assert.Equal(true, entries[0].Properties["PartialOutboundPermissionAutoReset"]);
        Assert.Equal(false, entries[1].Properties["PartialOutboundPermissionAutoReset"]);
        Assert.Equal(true, entries[1].Properties["IdempotentReplay"]);
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocUid) CreateOutboundDraft(
        bool permissionBefore)
    {
        const string docUid = "doc-outbound-permission-reset";
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 10, Code = "FG", Name = "Готовая продукция" });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedPartner(new Partner { Id = 200, Code = "C-200", Name = "Клиент" });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-020",
            Type = OrderType.Customer,
            PartnerId = 200,
            Status = OrderStatus.Accepted,
            AllowPartialOutbound = permissionBefore,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 100,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            Id = 202,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 100,
            ItemName = "Горчица",
            QtyPlanned = 5,
            ToLocationId = 10,
            ToLocationCode = "FG",
            ToHu = "HU-OUT-20"
        });
        harness.SeedBalance(100, 10, 5, "HU-OUT-20");
        harness.SeedDoc(new Doc
        {
            Id = 30,
            DocRef = "OUT-030",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 20,
            OrderRef = "SO-020",
            PartnerId = 200,
            CreatedAt = DateTime.UtcNow
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 30,
            OrderLineId = 201,
            ItemId = 100,
            Qty = 5,
            FromLocationId = 10,
            FromHu = "HU-OUT-20"
        });

        var apiStore = new InMemoryApiDocStore();
        apiStore.AddApiDoc(
            docUid,
            docId: 30,
            status: "DRAFT",
            docType: "OUTBOUND",
            docRef: "OUT-030",
            partnerId: 200,
            fromLocationId: 10,
            toLocationId: null,
            fromHu: "HU-OUT-20",
            toHu: null,
            deviceId: "TSD-01");
        return (harness, apiStore, docUid);
    }

    private static CapturedLogEntry[] DocumentCloseEntries(CapturingLoggerProvider provider) =>
        provider.Entries
            .Where(entry => string.Equals(
                entry.Properties.GetValueOrDefault("Operation")?.ToString(),
                "CloseDocument",
                StringComparison.Ordinal))
            .ToArray();
}
