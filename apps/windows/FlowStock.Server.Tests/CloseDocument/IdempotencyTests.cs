using FlowStock.Core.Models;
using System.Net;
using System.Net.Http.Json;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class IdempotencyTests
{
    [Fact]
    public void RepeatedClose_DoesNotDuplicateLedger()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "IN-2026-000003",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 13, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item { Id = 100, Name = "Горчица" });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            ItemId = 100,
            Qty = 12,
            ToLocationId = 10
        });

        var service = harness.CreateService();

        var first = service.TryCloseDoc(1, allowNegative: false);
        var second = service.TryCloseDoc(1, allowNegative: false);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains(second.Errors, error => error.Contains("уже закрыт", StringComparison.OrdinalIgnoreCase));
        Assert.Single(harness.LedgerEntries);
    }

    [Fact]
    public async Task HttpAlreadyClosed_NewEventId_ReturnsCanonicalNoOp()
    {
        var (harness, apiStore, docUid) = CloseDocumentHttpScenario.CreateInboundDraft();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var first = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-gap-001", DeviceId = "TSD-01" });
        using var second = await host.Client.PostAsJsonAsync(
            $"/api/docs/{docUid}/close",
            new CloseDocRequest { EventId = "evt-close-gap-002", DeviceId = "TSD-01" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstPayload = await first.Content.ReadFromJsonAsync<CloseDocResponse>();
        var firstClosedAt = harness.GetDoc(1).ClosedAt;
        var secondPayload = await second.Content.ReadFromJsonAsync<CloseDocResponse>();

        Assert.NotNull(firstPayload);
        Assert.NotNull(secondPayload);
        Assert.Equal("CLOSED", firstPayload!.Result);
        Assert.False(firstPayload.IdempotentReplay);
        Assert.False(firstPayload.AlreadyClosed);

        Assert.True(secondPayload!.Ok);
        Assert.True(secondPayload.Closed);
        Assert.Equal(docUid, secondPayload.DocUid);
        Assert.Equal("IN-2026-000010", secondPayload.DocRef);
        Assert.Equal("CLOSED", secondPayload.DocStatus);
        Assert.Equal("ALREADY_CLOSED", secondPayload.Result);
        Assert.False(secondPayload.IdempotentReplay);
        Assert.True(secondPayload.AlreadyClosed);
        Assert.Empty(secondPayload.Errors);
        Assert.Empty(secondPayload.Warnings);

        Assert.Single(harness.LedgerEntries);
        var doc = harness.GetDoc(1);
        Assert.Equal(DocStatus.Closed, doc.Status);
        Assert.Equal(firstClosedAt, doc.ClosedAt);
        Assert.Equal("CLOSED", apiStore.GetApiDoc(docUid)?.Status);
        Assert.Equal(2, apiStore.CountEvents("DOC_CLOSE", docUid));
        Assert.NotNull(apiStore.GetEvent("evt-close-gap-002"));
    }
}
