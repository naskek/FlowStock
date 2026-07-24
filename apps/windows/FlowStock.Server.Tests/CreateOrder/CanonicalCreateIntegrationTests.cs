using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateOrder.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using FlowStock.Server;

namespace FlowStock.Server.Tests.CreateOrder;

[Collection("CreateOrder")]
public sealed class CanonicalCreateIntegrationTests
{
    [Fact]
    public async Task SuccessfulCreateCustomer_ReturnsOrderIdOrderRefAndStatus()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                OrderRef = "001",
                Type = "CUSTOMER",
                PartnerId = 200,
                Status = "DRAFT",
                Lines =
                [
                    new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1001, QtyOrdered = 12 },
                    new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1002, QtyOrdered = 5 }
                ]
            });

        Assert.True(payload.Ok);
        Assert.Equal("CREATED", payload.Result);
        Assert.True(payload.OrderId > 0);
        Assert.Equal("001", payload.OrderRef);
        Assert.False(payload.OrderRefChanged);
        Assert.Equal("CUSTOMER", payload.Type);
        Assert.Equal("IN_PROGRESS", payload.Status);
        Assert.Equal(1, harness.OrderCount);
        Assert.Equal(2, harness.TotalOrderLineCount);

        var order = harness.GetOrder(payload.OrderId);
        Assert.Equal(OrderType.Customer, order.Type);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(200, order.PartnerId);
    }

    [Fact]
    public async Task SuccessfulCreateInternal_ReturnsCreatedOrder()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateInternalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "INTERNAL",
                Status = "ACCEPTED",
                Lines =
                [
                    new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1001, QtyOrdered = 20 }
                ]
            });

        Assert.True(payload.Ok);
        Assert.Equal("CREATED", payload.Result);
        Assert.True(payload.OrderId > 0);
        Assert.Equal("INTERNAL", payload.Type);
        Assert.Equal("IN_PROGRESS", payload.Status);
        Assert.Equal(1, payload.LineCount);
        Assert.Equal(1, harness.OrderCount);
        Assert.Equal(1, harness.TotalOrderLineCount);

        var order = harness.GetOrder(payload.OrderId);
        Assert.Equal(OrderType.Internal, order.Type);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Null(order.PartnerId);
    }

    [Fact]
    public async Task SuccessfulCreateCustomer_NormalizesProductionPurposeToCustomerOrder()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new CreateOrderHttpApi.CreateOrderLineRequest
                    {
                        ItemId = 1001,
                        QtyOrdered = 10,
                        ProductionPurpose = "INTERNAL_STOCK"
                    }
                ]
            });

        Assert.True(payload.Ok);

        var lines = harness.GetOrderLines(payload.OrderId).ToArray();
        Assert.Single(lines);
        Assert.Equal(ProductionLinePurpose.CustomerOrder, lines[0].ProductionPurpose);
    }

    [Fact]
    public async Task SuccessfulCreateInternal_NormalizesProductionPurposeToInternalStock()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateInternalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "INTERNAL",
                Lines =
                [
                    new CreateOrderHttpApi.CreateOrderLineRequest
                    {
                        ItemId = 1001,
                        QtyOrdered = 756,
                        ProductionPurpose = "CUSTOMER_ORDER"
                    },
                    new CreateOrderHttpApi.CreateOrderLineRequest
                    {
                        ItemId = 1001,
                        QtyOrdered = 1134,
                        ProductionPurpose = "INTERNAL_STOCK"
                    }
                ]
            });

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.LineCount);

        var lines = harness.GetOrderLines(payload.OrderId).ToArray();
        Assert.Single(lines);
        Assert.Equal(1890, lines[0].QtyOrdered);
        Assert.Equal(ProductionLinePurpose.InternalStock, lines[0].ProductionPurpose);
    }

    [Fact]
    public async Task Customer_line_copies_price_and_vat_snapshots()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines = [new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1001, QtyOrdered = 2.5 }]
            });

        var line = Assert.Single(harness.GetOrderLines(payload.OrderId));
        Assert.Equal(100m, line.UnitPriceGross);
        Assert.Equal(22m, line.VatRate);
    }

    [Fact]
    public async Task Manual_override_is_snapshotted_but_does_not_override_vat()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new CreateOrderHttpApi.CreateOrderLineRequest
                    {
                        ItemId = 1001,
                        QtyOrdered = 1,
                        ChangeUnitPriceGross = true,
                        UnitPriceGross = 0m
                    }
                ]
            });

        var line = Assert.Single(harness.GetOrderLines(payload.OrderId));
        Assert.Equal(0m, line.UnitPriceGross);
        Assert.Equal(22m, line.VatRate);
    }

    [Fact]
    public async Task Missing_vat_rolls_back_entire_customer_order()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Без НДС",
            DefaultSalePriceGross = 120m
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders",
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1001, QtyOrdered = 1 },
                    new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1002, QtyOrdered = 1 }
                ]
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResult>();
        Assert.NotNull(error);
        Assert.Equal(CommercialTermsResolver.ItemSaleVatRateRequired, error.Error);
        Assert.Contains("карточке товара", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);
    }

    [Fact]
    public async Task Internal_line_remains_without_commercial_snapshots()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateInternalScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrderHttpApi.CreateAsync(
            host.Client,
            new CreateOrderHttpApi.CreateOrderRequest
            {
                Type = "INTERNAL",
                Lines = [new CreateOrderHttpApi.CreateOrderLineRequest { ItemId = 1001, QtyOrdered = 2 }]
            });

        var line = Assert.Single(harness.GetOrderLines(payload.OrderId));
        Assert.Null(line.UnitPriceGross);
        Assert.Null(line.VatRate);
    }
}
