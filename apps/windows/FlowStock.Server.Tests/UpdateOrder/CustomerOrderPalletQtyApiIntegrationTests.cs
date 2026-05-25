using System.Net;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class CustomerOrderPalletQtyApiIntegrationTests
{
    [Fact]
    public async Task DecreaseWithUnfilledProductionPlan_PersistsQtyAndCancelsObsoletePallets()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1200);
        var palletService = new ProductionPalletService(fixture.Harness.Store);
        var plan = palletService.PlanOrder(fixture.OrderId);
        Assert.Equal(2, fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Count);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 600));

        Assert.True(payload.Ok);
        Assert.Equal(600, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
        var pallets = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        var active = pallets
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(active);
        Assert.Equal(600, active.Sum(pallet => pallet.PlannedQty), 3);
        Assert.Single(pallets, pallet => string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase));

        using var linesResponse = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        using var document = JsonDocument.Parse(await linesResponse.Content.ReadAsStringAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(600, line.GetProperty("qty_ordered").GetDouble(), 3);
    }

    [Fact]
    public async Task DecreaseBelowFilledCustomerPallet_ReturnsValidationWithBlockingHu()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1200);
        var palletService = new ProductionPalletService(fixture.Harness.Store);
        var plan = palletService.PlanOrder(fixture.OrderId);
        var filledHu = fixture.Harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .First()
            .HuCode;
        Assert.True(palletService.Fill(filledHu, "TSD-01").Success);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        using var response = await UpdateOrderHttpApi.PutRawAsync(
            host.Client,
            fixture.OrderId,
            BuildRawJson(fixture, qtyOrdered: 500));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(error.Ok);
        Assert.Equal("ORDER_LINE_QTY_BELOW_COVERAGE", error.Error);
        Assert.Contains(filledHu, error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FILLED", error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1200, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
    }

    [Fact]
    public async Task PutCustomerQtyChange_LinesApiDoesNotSilentlyReturnOldQty()
    {
        var fixture = CreateCustomerFixture(orderedQty: 1134);
        await using var host = await CloseDocumentHttpHost.StartAsync(fixture.Harness, fixture.ApiStore);

        var payload = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            fixture.OrderId,
            BuildUpdateRequest(fixture, qtyOrdered: 756));

        Assert.True(payload.Ok);
        using var linesResponse = await host.Client.GetAsync($"/api/orders/{fixture.OrderId}/lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        using var document = JsonDocument.Parse(await linesResponse.Content.ReadAsStringAsync());
        var line = document.RootElement.EnumerateArray().Single();
        Assert.Equal(756, line.GetProperty("qty_ordered").GetDouble(), 3);
        Assert.Equal(756, Assert.Single(fixture.Harness.Store.GetOrderLines(fixture.OrderId)).QtyOrdered, 3);
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest BuildUpdateRequest(CustomerFixture fixture, double qtyOrdered)
    {
        return new UpdateOrderHttpApi.UpdateOrderRequest
        {
            OrderRef = fixture.OrderRef,
            Type = "CUSTOMER",
            PartnerId = fixture.PartnerId,
            Status = "IN_PROGRESS",
            Lines =
            [
                new UpdateOrderHttpApi.UpdateOrderLineRequest
                {
                    ItemId = fixture.ItemId,
                    QtyOrdered = qtyOrdered
                }
            ]
        };
    }

    private static string BuildRawJson(CustomerFixture fixture, double qtyOrdered)
    {
        return $$"""
                 {
                   "order_ref": "{{fixture.OrderRef}}",
                   "type": "CUSTOMER",
                   "partner_id": {{fixture.PartnerId}},
                   "status": "IN_PROGRESS",
                   "lines": [
                     {
                       "item_id": {{fixture.ItemId}},
                       "qty_ordered": {{qtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                     }
                   ]
                 }
                 """;
    }

    private static CustomerFixture CreateCustomerFixture(double orderedQty)
    {
        var harness = new CloseDocumentHarness();
        const long orderId = 55;
        const long orderLineId = 143;
        const long itemId = 1001;
        const long partnerId = 200;
        harness.SeedPartner(new Partner
        {
            Id = partnerId,
            Code = "CUST-200",
            Name = "Покупатель",
            CreatedAt = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = "Аджика",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "055",
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = orderedQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return new CustomerFixture(harness, new InMemoryApiDocStore(), orderId, orderLineId, itemId, partnerId, "055");
    }

    private sealed record CustomerFixture(
        CloseDocumentHarness Harness,
        InMemoryApiDocStore ApiStore,
        long OrderId,
        long OrderLineId,
        long ItemId,
        long PartnerId,
        string OrderRef);
}
