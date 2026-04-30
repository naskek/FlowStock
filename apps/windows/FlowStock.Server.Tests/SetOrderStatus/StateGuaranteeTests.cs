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

        var payload = await SetOrderStatusHttpApi.ChangeAsync(host.Client, orderId, "CANCELLED");
        Assert.True(payload.Ok);
        Assert.Equal("STATUS_CHANGED", payload.Result);

        Assert.Equal(docsBefore, harness.DocCount);
        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
        Assert.Equal(FlowStock.Core.Models.OrderStatus.Cancelled, harness.GetOrder(orderId).Status);
    }
}
