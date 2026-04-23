using System.Net;
using System.Net.Http.Json;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocLine.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocLine;

public sealed class ValidationTests
{
    [Fact]
    public async Task MissingEventId_Fails()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-val-001", "evt-line-val-create-001");

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs/line-val-001/lines",
            new AddDocLineRequest
            {
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("MISSING_EVENT_ID", payload.Error);
        Assert.Empty(harness.GetDocLines(created.Doc!.Id));
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE", "line-val-001"));
    }

    [Fact]
    public async Task UnknownDocUid_Fails()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateEmptyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs/line-val-002/lines",
            new AddDocLineRequest
            {
                EventId = "evt-line-val-002",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.NotFound);
        Assert.False(payload.Ok);
        Assert.Equal("DOC_NOT_FOUND", payload.Error);
        Assert.Equal(0, harness.TotalDocLineCount);
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE"));
    }

    [Fact]
    public async Task DocStatusNotDraft_Fails()
    {
        var (harness, apiStore, docUid) = CreateDocLineHttpScenario.CreateClosedInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/lines",
            new AddDocLineRequest
            {
                EventId = "evt-line-val-003",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("DOC_NOT_DRAFT", payload.Error);
        Assert.Empty(harness.GetDocLines(1));
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE", docUid));
    }

    [Fact]
    public async Task UnknownItem_Fails()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-val-004", "evt-line-val-create-004");

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs/line-val-004/lines",
            new AddDocLineRequest
            {
                EventId = "evt-line-val-004",
                DeviceId = "API-01",
                ItemId = 999,
                Qty = 5
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("UNKNOWN_ITEM", payload.Error);
        Assert.Empty(harness.GetDocLines(created.Doc!.Id));
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE", "line-val-004"));
    }

    [Fact]
    public async Task InactiveItem_Fails()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        harness.SeedItem(new FlowStock.Core.Models.Item
        {
            Id = 100,
            Name = "Mustard",
            Barcode = "4660011933641",
            IsActive = false
        });

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-val-006", "evt-line-val-create-006");

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs/line-val-006/lines",
            new AddDocLineRequest
            {
                EventId = "evt-line-val-006",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 5
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("ITEM_INACTIVE", payload.Error);
        Assert.Empty(harness.GetDocLines(created.Doc!.Id));
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE", "line-val-006"));
    }

    [Fact]
    public async Task QtyLessOrEqualZero_Fails()
    {
        var (harness, apiStore) = CreateDocLineHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        var created = await CreateDocLineHttpScenario.CreateInboundDraftAsync(host.Client, "line-val-005", "evt-line-val-create-005");

        using var response = await host.Client.PostAsJsonAsync(
            "/api/docs/line-val-005/lines",
            new AddDocLineRequest
            {
                EventId = "evt-line-val-005",
                DeviceId = "API-01",
                ItemId = 100,
                Qty = 0
            });

        var payload = await CreateDocLineHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("INVALID_QTY", payload.Error);
        Assert.Empty(harness.GetDocLines(created.Doc!.Id));
        Assert.Equal(0, apiStore.CountEvents("DOC_LINE", "line-val-005"));
    }
}
