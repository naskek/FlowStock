using System.Globalization;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class CanonicalUpdateIntegrationTests
{
    [Fact]
    public async Task SuccessfulUpdateExistingOrder_ReturnsOrderIdOrderRefAndStatus()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "002",
                Type = "CUSTOMER",
                PartnerId = 202,
                DueDate = "2026-03-25",
                Status = "IN_PROGRESS",
                Comment = "Обновлено через API",
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1002, QtyOrdered = 7 },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1003, QtyOrdered = 4 }
                ]
            });

        Assert.True(payload.Ok);
        Assert.Equal("UPDATED", payload.Result);
        Assert.Equal(orderId, payload.OrderId);
        Assert.Equal("002", payload.OrderRef);
        Assert.False(payload.OrderRefChanged);
        Assert.Equal("CUSTOMER", payload.Type);
        Assert.Equal("IN_PROGRESS", payload.Status);
        Assert.Equal(2, payload.LineCount);

        var order = harness.GetOrder(orderId);
        Assert.Equal("002", order.OrderRef);
        Assert.Equal(OrderType.Customer, order.Type);
        Assert.Equal(202, order.PartnerId);
        Assert.Equal(new DateTime(2026, 3, 25), order.DueDate);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal("Обновлено через API", order.Comment);
    }

    [Fact]
    public async Task SuccessfulUpdateInternal_NormalizesProductionPurposeToInternalStock()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateInternalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "INT-002",
                Type = "INTERNAL",
                Comment = "Обновлено назначение строк",
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        ItemId = 1001,
                        QtyOrdered = 756,
                        ProductionPurpose = "CUSTOMER_ORDER"
                    },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        ItemId = 1001,
                        QtyOrdered = 1134,
                        ProductionPurpose = "INTERNAL_STOCK"
                    }
                ]
            });

        Assert.True(payload.Ok);
        Assert.Equal(orderId, payload.OrderId);
        Assert.Equal(1, payload.LineCount);

        var lines = harness.GetOrderLines(orderId).ToArray();
        Assert.Single(lines);
        Assert.Equal(1890, lines[0].QtyOrdered);
        Assert.Equal(ProductionLinePurpose.InternalStock, lines[0].ProductionPurpose);
    }
}
