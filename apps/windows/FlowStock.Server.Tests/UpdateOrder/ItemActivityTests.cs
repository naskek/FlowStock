using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class ItemActivityTests
{
    [Theory]
    [InlineData(10, true)]
    [InlineData(8, true)]
    [InlineData(12, false)]
    public async Task ExistingInactiveLine_AllowsSameOrDecrease_BlocksIncrease(double qty, bool expectedSuccess)
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 101,
                ItemId = 1001,
                QtyOrdered = qty
            }));

        if (expectedSuccess)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(qty, harness.GetOrderLines(orderId).Single(line => line.Id == 101).QtyOrdered);
            return;
        }

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.Error);
        Assert.Equal(10, harness.GetOrderLines(orderId).Single(line => line.Id == 101).QtyOrdered);
    }

    [Fact]
    public async Task AddingInactiveLine_RollsBackOtherLineChanges()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedItem(InactiveCommercialItem(1003, "Неактивный соус"));
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "CHANGED",
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new() { OrderLineId = 101, ItemId = 1001, QtyOrdered = 9 },
                    new() { OrderLineId = 102, ItemId = 1002, QtyOrdered = 5 },
                    new() { ItemId = 1003, QtyOrdered = 1 }
                ]
            });

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.Error);
        Assert.Equal("001", harness.GetOrder(orderId).OrderRef);
        Assert.Equal(10, harness.GetOrderLines(orderId).Single(line => line.Id == 101).QtyOrdered);
        Assert.DoesNotContain(harness.GetOrderLines(orderId), line => line.ItemId == 1003);
    }

    [Fact]
    public async Task RemovingExistingInactiveLine_RemainsAllowed()
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "001",
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines = [new() { OrderLineId = 102, ItemId = 1002, QtyOrdered = 5 }]
            });

        Assert.DoesNotContain(harness.GetOrderLines(orderId), line => line.Id == 101);
    }

    [Fact]
    public async Task ExistingInactiveLine_RemainsAvailableFromReadEndpoint()
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.GetAsync($"/api/orders/{orderId}/lines");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Contains(
            payload.RootElement.EnumerateArray(),
            line => line.GetProperty("id").GetInt64() == 101
                    && line.GetProperty("item_id").GetInt64() == 1001);
    }

    [Fact]
    public async Task Reactivation_AllowsIncreaseAgain()
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        harness.SeedItem(ActiveCommercialItem(1001, "Горчица"));
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 101,
                ItemId = 1001,
                QtyOrdered = 12
            }));

        Assert.Equal(12, harness.GetOrderLines(orderId).Single(line => line.Id == 101).QtyOrdered);
    }

    [Fact]
    public async Task ExplicitItemReplacement_IsRejectedBeforeActivityGuard()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedItem(InactiveCommercialItem(1003, "Неактивный соус"));
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 101,
                ItemId = 1003,
                QtyOrdered = 10
            }));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("ORDER_LINE_NOT_FOUND", error.Error);
        Assert.Equal(1001, harness.GetOrderLines(orderId).Single(line => line.Id == 101).ItemId);
    }

    [Fact]
    public async Task LegacyFallback_CannotIncreaseInactiveLine()
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                ItemId = 1001,
                QtyOrdered = 11
            }));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.Error);
        Assert.Equal(10, harness.GetOrderLines(orderId).Single(line => line.Id == 101).QtyOrdered);
    }

    [Fact]
    public async Task ExplicitDuplicateSelection_PreservesSelectedIdAndMutatesOnlyIt()
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 103,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = 20,
            UnitPriceGross = 150m,
            VatRate = 22m,
            ProductionPalletGroup = "OLD"
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 103,
                ItemId = 1001,
                QtyOrdered = 15,
                ChangeUnitPriceGross = true,
                UnitPriceGross = 160m,
                ProductionPalletGroup = "NEW"
            }));

        var selected = Assert.Single(harness.GetOrderLines(orderId), line => line.ItemId == 1001);
        Assert.Equal(103, selected.Id);
        Assert.Equal(15, selected.QtyOrdered);
        Assert.Equal(160m, selected.UnitPriceGross);
        Assert.Equal("NEW", selected.ProductionPalletGroup);
        Assert.DoesNotContain(harness.GetOrderLines(orderId), line => line.Id == 101);
    }

    [Fact]
    public async Task ExplicitDuplicateSelection_InactiveIncreaseRollsBackBothLines()
    {
        var (harness, apiStore, orderId) = CreateInactiveScenario();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 103,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = 20,
            UnitPriceGross = 150m,
            VatRate = 22m
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 103,
                ItemId = 1001,
                QtyOrdered = 25
            }));

        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.Error);
        Assert.Equal(10, harness.GetOrderLines(orderId).Single(line => line.Id == 101).QtyOrdered);
        Assert.Equal(20, harness.GetOrderLines(orderId).Single(line => line.Id == 103).QtyOrdered);
    }

    [Fact]
    public async Task ExplicitDuplicateSelection_WhenOtherDuplicateCleanupIsBlocked_RollsBackSelectedMutation()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 103,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = 20,
            UnitPriceGross = 150m,
            VatRate = 22m,
            ProductionPalletGroup = "OLD"
        });
        harness.SeedOrderReceiptPlanLines(
            orderId,
            new OrderReceiptPlanLine
            {
                Id = 5001,
                OrderId = orderId,
                OrderLineId = 101,
                ItemId = 1001,
                QtyPlanned = 10,
                ToHu = "HU-DUPLICATE-BLOCK",
                SortOrder = 0
            });
        harness.SeedBalance(1001, 1, 10, "HU-DUPLICATE-BLOCK");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            BuildRequest(new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 103,
                ItemId = 1001,
                QtyOrdered = 15,
                ChangeUnitPriceGross = true,
                UnitPriceGross = 160m,
                ProductionPalletGroup = "NEW"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var selected = harness.GetOrderLines(orderId).Single(line => line.Id == 103);
        Assert.Equal(20, selected.QtyOrdered);
        Assert.Equal(150m, selected.UnitPriceGross);
        Assert.Equal("OLD", selected.ProductionPalletGroup);
        Assert.Contains(harness.GetOrderLines(orderId), line => line.Id == 101);
        Assert.Contains(harness.GetOrderReceiptPlanLines(orderId), line => line.OrderLineId == 101);
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore, long OrderId) CreateInactiveScenario()
    {
        var scenario = UpdateOrderHttpScenario.CreateCustomerScenario();
        scenario.Harness.SeedItem(InactiveCommercialItem(1001, "Горчица"));
        return scenario;
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest BuildRequest(
        UpdateOrderHttpApi.UpdateOrderLineRequest firstLine) => new()
    {
        OrderRef = "001",
        Type = "CUSTOMER",
        PartnerId = 200,
        Lines =
        [
            firstLine,
            new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 102,
                ItemId = 1002,
                QtyOrdered = 5
            }
        ]
    };

    private static Item InactiveCommercialItem(long id, string name) => new()
    {
        Id = id,
        Name = name,
        IsActive = false,
        DefaultSalePriceGross = 100m,
        DefaultSaleVatRateId = 1,
        DefaultSaleVatRate = 22m,
        DefaultSaleVatRateIsActive = true
    };

    private static Item ActiveCommercialItem(long id, string name) => new()
    {
        Id = id,
        Name = name,
        IsActive = true,
        DefaultSalePriceGross = 100m,
        DefaultSaleVatRateId = 1,
        DefaultSaleVatRate = 22m,
        DefaultSaleVatRateIsActive = true
    };
}
