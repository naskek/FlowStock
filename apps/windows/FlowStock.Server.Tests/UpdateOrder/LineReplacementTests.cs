using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class LineReplacementTests
{
    [Fact]
    public async Task LineSnapshotReplacement_Works()
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
                PartnerId = 200,
                Status = "ACCEPTED",
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1002, QtyOrdered = 7 },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1003, QtyOrdered = 4 }
                ]
            });

        Assert.Equal(2, payload.LineCount);

        var lines = harness.GetOrderLines(orderId);
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, line => line.ItemId == 1001);
        Assert.Contains(lines, line => line.ItemId == 1002 && Math.Abs(line.QtyOrdered - 7) < 0.000001);
        Assert.Contains(lines, line => line.ItemId == 1003 && Math.Abs(line.QtyOrdered - 4) < 0.000001);
    }

    [Fact]
    public async Task DuplicateItemLines_Normalize()
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
                PartnerId = 200,
                Status = "ACCEPTED",
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1001, QtyOrdered = 2 },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1001, QtyOrdered = 3 },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1002, QtyOrdered = 1 }
                ]
            });

        Assert.Equal(2, payload.LineCount);

        var lines = harness.GetOrderLines(orderId);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.ItemId == 1001 && Math.Abs(line.QtyOrdered - 5) < 0.000001);
        Assert.Contains(lines, line => line.ItemId == 1002 && Math.Abs(line.QtyOrdered - 1) < 0.000001);
    }

    [Fact]
    public async Task DeleteReservedCustomerLine_IsBlockedByProtectedReadyHuBinding()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedOrderReceiptPlanLines(
            orderId,
            new OrderReceiptPlanLine
            {
                Id = 5001,
                OrderId = orderId,
                OrderLineId = 101,
                ItemId = 1001,
                QtyPlanned = 10,
                ToHu = "HU-001",
                SortOrder = 0
            });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "002",
                Type = "CUSTOMER",
                PartnerId = 200,
                Status = "ACCEPTED",
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest { ItemId = 1002, QtyOrdered = 5 }
                ]
            });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var lines = harness.GetOrderLines(orderId);
        Assert.Contains(lines, line => line.Id == 101 && line.ItemId == 1001);
        Assert.Contains(harness.GetOrderReceiptPlanLines(orderId), line => line.OrderLineId == 101);
    }
}
