using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.SetOrderStatus.Infrastructure;

namespace FlowStock.Server.Tests.SetOrderStatus;

[Collection("SetOrderStatus")]
public sealed class TransitionRulesTests
{
    [Fact]
    public async Task ShippedTargetStatus_IsRejected_WhenManualStatusChangeDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = "SHIPPED" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);
        Assert.Equal(OrderStatus.Draft, harness.GetOrder(orderId).Status);
    }

    [Fact]
    public async Task DraftTargetStatus_IsRejected_WhenManualStatusChangeDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = "DRAFT" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);
        Assert.Equal(OrderStatus.Draft, harness.GetOrder(orderId).Status);
    }

    [Fact]
    public async Task ExistingShippedOrder_CannotBeChanged_WhenManualStatusChangeDisabled()
    {
        var (harness, apiStore, orderId) = SetOrderStatusHttpScenario.CreateShippedCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = "ACCEPTED" });
        var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_STATUS_MANUAL_DISABLED", payload.Error);
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(orderId).Status);
    }
}
