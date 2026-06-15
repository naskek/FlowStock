using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.DeleteDocLine.Infrastructure;

namespace FlowStock.Server.Tests.DeleteDocLine;

public sealed class ValidationTests
{
    [Fact]
    public async Task UnknownLineId_Fails()
    {
        var scenario = await DeleteDocLineHttpScenario.StartInboundDraftWithLineAsync(
            "line-delete-val-001",
            "evt-line-delete-create-201",
            "evt-line-delete-add-201");
        await using var host = scenario.Host;

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{scenario.DocUid}/lines/delete",
            new DeleteDocLineRequest
            {
                EventId = "evt-line-delete-val-001",
                DeviceId = "API-01",
                LineId = 999999
            });

        var payload = await DeleteDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("UNKNOWN_LINE", payload.Error);
        Assert.Single(scenario.Harness.GetDocLines(scenario.DocId));
        Assert.Single(scenario.Harness.GetAllDocLines(scenario.DocId));
    }

    [Fact]
    public async Task NonDraftDocument_Fails()
    {
        var (harness, apiStore, docUid) = CreateClosedScenarioWithLine();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/lines/delete",
            new DeleteDocLineRequest
            {
                EventId = "evt-line-delete-val-002",
                DeviceId = "API-01",
                LineId = 1
            });

        var payload = await DeleteDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("DOC_NOT_DRAFT", payload.Error);
        Assert.Single(harness.GetDocLines(1));
        Assert.Single(harness.GetAllDocLines(1));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task ProductionReceiptDraft_FailsWithoutTombstone()
    {
        var (harness, apiStore, docUid) = CreateProductionReceiptScenarioWithLine();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/lines/delete",
            new DeleteDocLineRequest
            {
                EventId = "evt-line-delete-prd-001",
                DeviceId = "API-01",
                LineId = 1
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResult>();
        Assert.NotNull(payload);
        Assert.False(payload.Ok);
        Assert.Equal(DocumentService.ProductionReceiptLineDeleteForbiddenCode, payload.Error);
        Assert.Equal(DocumentService.ProductionReceiptLineDeleteForbiddenMessage, payload.Message);
        Assert.Single(harness.GetDocLines(1));
        Assert.Single(harness.GetAllDocLines(1));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void DocumentService_ProductionReceiptDraft_BlocksSingleAndBatchDelete()
    {
        var (harness, _, _) = CreateProductionReceiptScenarioWithLine();
        var service = harness.CreateService();

        var singleError = Assert.Throws<InvalidOperationException>(() => service.DeleteDocLine(1, 1));
        var batchError = Assert.Throws<InvalidOperationException>(() => service.DeleteDocLines(1, [1]));

        Assert.Equal(DocumentService.ProductionReceiptLineDeleteForbiddenMessage, singleError.Message);
        Assert.Equal(DocumentService.ProductionReceiptLineDeleteForbiddenMessage, batchError.Message);
        Assert.Single(harness.GetDocLines(1));
        Assert.Single(harness.GetAllDocLines(1));
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocUid) CreateClosedScenarioWithLine()
    {
        const string docUid = "line-delete-closed-001";
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
            DocRef = "IN-LINE-DELETE-CLOSED-001",
            Type = DocType.Inbound,
            Status = DocStatus.Closed,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 3, 10, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 1,
            DocId = 1,
            ItemId = 100,
            Qty = 5,
            ToLocationId = 10,
            UomCode = "BOX"
        });

        var apiStore = new InMemoryApiDocStore();
        apiStore.AddApiDoc(
            docUid,
            docId: 1,
            status: "CLOSED",
            docType: "INBOUND",
            docRef: "IN-LINE-DELETE-CLOSED-001",
            partnerId: null,
            fromLocationId: null,
            toLocationId: 10,
            fromHu: null,
            toHu: null,
            deviceId: "API-01");

        return (harness, apiStore, docUid);
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, string DocUid) CreateProductionReceiptScenarioWithLine()
    {
        const string docUid = "line-delete-prd-001";
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-LINE-DELETE-001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 1,
            DocId = 1,
            ItemId = 100,
            Qty = 5,
            ToLocationId = 10,
            ToHu = "HU-PRD-001",
            UomCode = "BOX"
        });

        var apiStore = new InMemoryApiDocStore();
        apiStore.AddApiDoc(
            docUid,
            docId: 1,
            status: "DRAFT",
            docType: "PRODUCTION_RECEIPT",
            docRef: "PRD-LINE-DELETE-001",
            partnerId: null,
            fromLocationId: null,
            toLocationId: 10,
            fromHu: null,
            toHu: "HU-PRD-001",
            deviceId: "API-01");

        return (harness, apiStore, docUid);
    }
}
