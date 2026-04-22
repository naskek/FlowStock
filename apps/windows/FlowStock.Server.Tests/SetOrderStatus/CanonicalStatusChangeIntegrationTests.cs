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
    public async Task ManualStatusChange_IsDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = "ACCEPTED" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);
        Assert.Equal(OrderStatus.Draft, harness.GetOrder(orderId).Status);
    }
}
