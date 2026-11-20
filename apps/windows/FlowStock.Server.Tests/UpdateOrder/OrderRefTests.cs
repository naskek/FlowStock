using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class OrderRefTests
{
    [Fact]
    public async Task CollidingRequestedOrderRef_ReturnsReplacementWithOrderRefChanged()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateOrderRefCollisionScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "777",
                Type = "CUSTOMER",
                PartnerId = 200,
                Status = "ACCEPTED",
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1001, QtyOrdered = 10 }
                ]
            });

        Assert.True(payload.Ok);
        Assert.True(payload.OrderRefChanged);
        Assert.NotEqual("777", payload.OrderRef);
        Assert.Equal(payload.OrderRef, harness.GetOrder(orderId).OrderRef);
    }
    [Theory]
    [InlineData("001", "001")]
    [InlineData(" 001 ", "001")]
    [InlineData("NEW-UNIQUE-REF", "NEW-UNIQUE-REF")]
    public async Task CurrentOrUniqueRequestedRef_IsNotRenumbered(string requestedRef, string expectedRef)
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client, orderId, CreateRequest(requestedRef));

        Assert.True(payload.Ok);
        Assert.False(payload.OrderRefChanged);
        Assert.Equal(expectedRef, payload.OrderRef);
        Assert.Equal(expectedRef, harness.GetOrder(orderId).OrderRef);
    }

    [Fact]
    public async Task CaseInsensitiveCollision_RenumbersWithoutChangingOtherOrder()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedOrder(new Order
        {
            Id = 55,
            OrderRef = "Mixed-Case-Ref",
            Type = OrderType.Customer,
            PartnerId = 200,
            Status = OrderStatus.Cancelled,
            CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client, orderId, CreateRequest("mixed-case-ref"));

        Assert.True(payload.Ok);
        Assert.True(payload.OrderRefChanged);
        Assert.Equal("002", payload.OrderRef);
        Assert.Equal("Mixed-Case-Ref", harness.GetOrder(55).OrderRef);
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest CreateRequest(string requestedRef) => new()
    {
        OrderRef = requestedRef,
        Type = "CUSTOMER",
        PartnerId = 200,
        Status = "ACCEPTED",
        Lines =
        [
            new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1001, QtyOrdered = 10 }
        ]
    };

}
