using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.SetOrderStatus.Infrastructure;
using System.Net;
using System.Net.Http.Json;

namespace FlowStock.Server.Tests.SetOrderStatus;

[Collection("SetOrderStatus")]
public sealed class StateGuaranteeTests
{
    [Fact]
    public async Task StatusChange_DoesNotWriteDocsOrLedger()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var docsBefore = harness.DocCount;
        var ledgerBefore = harness.LedgerEntries.Count;

        using var response = await host.Client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = "IN_PROGRESS" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);

        Assert.Equal(docsBefore, harness.DocCount);
        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
    }
}
