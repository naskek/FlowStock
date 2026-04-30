using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.SetOrderStatus.Infrastructure;
using System.Net;
using System.Net.Http.Json;

namespace FlowStock.Server.Tests.SetOrderStatus;

[Collection("SetOrderStatus")]
public sealed class CanonicalStatusChangeIntegrationTests
{
    [Fact]
    public async Task CancelStatusChange_IsAllowed()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await SetOrderStatusHttpApi.ChangeAsync(host.Client, orderId, "CANCELLED");

        Assert.True(payload.Ok);
        Assert.Equal("STATUS_CHANGED", payload.Result);
        Assert.Equal(OrderStatus.Cancelled, harness.GetOrder(orderId).Status);
    }
}
