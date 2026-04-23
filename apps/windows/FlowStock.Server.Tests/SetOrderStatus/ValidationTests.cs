using System.Net;
using System.Net.Http.Json;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.SetOrderStatus.Infrastructure;

namespace FlowStock.Server.Tests.SetOrderStatus;

[Collection("SetOrderStatus")]
public sealed class ValidationTests
{
    [Fact]
    public async Task UnknownOrderId_ReturnsManualStatusDisabled()
    {
        var (harness, apiStore, _) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/orders/999/status", new { status = "ACCEPTED" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);
    }

    [Fact]
    public async Task InvalidStatus_ReturnsManualStatusDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = "WRONG" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);
    }
}
