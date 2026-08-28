using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.DeleteOrder.Infrastructure;

namespace FlowStock.Server.Tests.DeleteOrder;

[Collection("DeleteOrder")]
public sealed class ValidationTests
{
    [Fact]
    public async Task UnknownOrderId_Fails()
    {
        var (harness, apiStore, _) = DeleteOrderHttpScenario.CreateDraftCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync("/api/orders/999");
        var payload = await DeleteOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.NotFound);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_NOT_FOUND", payload.Error);
    }

    [Fact]
    public async Task NonDraftOrder_Fails()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateAcceptedCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync($"/api/orders/{orderId}");
        var payload = await DeleteOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_DELETE_FORBIDDEN_STATUS", payload.Error);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task OrderWithOutboundDocs_Fails()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateCustomerWithOutboundDocsScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync($"/api/orders/{orderId}");
        var payload = await DeleteOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_HAS_OUTBOUND_DOCS", payload.Error);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task OrderWithShipments_Fails()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateCustomerWithShipmentsScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync($"/api/orders/{orderId}");
        var payload = await DeleteOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_HAS_SHIPMENTS", payload.Error);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task OrderWithMarkingHistory_ReturnsStableDomainErrorAndMessage()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateDraftCustomerScenario();
        harness.SeedOrderMarkingHistoryDependencies(orderId, 301);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync($"/api/orders/{orderId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResult>();
        Assert.NotNull(payload);
        Assert.False(payload!.Ok);
        Assert.Equal(OrderMarkingHistoryDeleteException.OrderErrorCode, payload.Error);
        Assert.Contains("историю маркировки", payload.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task InternalOrderWithProductionDocs_Fails()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateInternalWithProductionDocsScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync($"/api/orders/{orderId}");
        var payload = await DeleteOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_HAS_PRODUCTION_DOCS", payload.Error);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }

    [Fact]
    public async Task InternalOrderWithProductionReceipts_Fails()
    {
        var (harness, apiStore, orderId) = DeleteOrderHttpScenario.CreateInternalWithReceiptsScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.DeleteAsync($"/api/orders/{orderId}");
        var payload = await DeleteOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);

        Assert.False(payload.Ok);
        Assert.Equal("ORDER_HAS_PRODUCTION_RECEIPTS", payload.Error);
        Assert.NotNull(harness.Store.GetOrder(orderId));
    }
}
