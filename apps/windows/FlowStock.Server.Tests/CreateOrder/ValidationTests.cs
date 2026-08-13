using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateOrder.Infrastructure;

namespace FlowStock.Server.Tests.CreateOrder;

[Collection("CreateOrder")]
public sealed class ValidationTests
{
    [Theory]
    [InlineData("CUSTOMER", 200L)]
    [InlineData("INTERNAL", null)]
    public async Task InactiveItem_FailsForBothOrderTypes(string type, long? partnerId)
    {
        var (harness, apiStore) = type == "CUSTOMER"
            ? CreateOrderHttpScenario.CreateCustomerScenario()
            : CreateOrderHttpScenario.CreateInternalScenario();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Неактивный товар",
            IsActive = false,
            DefaultSalePriceGross = 100m,
            DefaultSaleVatRateId = 1,
            DefaultSaleVatRate = 22m,
            DefaultSaleVatRateIsActive = true
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            $$"""
            {
              "type": "{{type}}",
              "partner_id": {{(partnerId?.ToString() ?? "null")}},
              "lines": [{ "item_id": 1001, "qty_ordered": 10 }]
            }
            """);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResult>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, payload.Error);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);
    }

    [Fact]
    public async Task CustomerWithoutPartner_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "status": "DRAFT",
              "lines": [{ "item_id": 1001, "qty_ordered": 10 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("MISSING_PARTNER_ID", payload.Error);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);
    }

    [Fact]
    public async Task SupplierPartner_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 201,
              "status": "DRAFT",
              "lines": [{ "item_id": 1001, "qty_ordered": 10 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("PARTNER_IS_SUPPLIER", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task UnknownPartner_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 999,
              "status": "DRAFT",
              "lines": [{ "item_id": 1001, "qty_ordered": 10 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("PARTNER_NOT_FOUND", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task InvalidDueDate_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 200,
              "due_date": "31-03-2026",
              "status": "DRAFT",
              "lines": [{ "item_id": 1001, "qty_ordered": 10 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("INVALID_DUE_DATE", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task MissingLines_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 200,
              "status": "DRAFT",
              "lines": []
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("MISSING_LINES", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task UnknownItem_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 200,
              "status": "DRAFT",
              "lines": [{ "item_id": 9999, "qty_ordered": 10 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("ITEM_NOT_FOUND", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task QtyOrderedLessOrEqualZero_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 200,
              "status": "DRAFT",
              "lines": [{ "item_id": 1001, "qty_ordered": 0 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("INVALID_QTY_ORDERED", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task ShippedStatus_Fails()
    {
        var (harness, apiStore) = CreateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await CreateOrderHttpApi.PostRawAsync(
            host.Client,
            """
            {
              "type": "CUSTOMER",
              "partner_id": 200,
              "status": "SHIPPED",
              "lines": [{ "item_id": 1001, "qty_ordered": 10 }]
            }
            """);

        var payload = await CreateOrderHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
        Assert.False(payload.Ok);
        Assert.Equal("SHIPPED_STATUS_FORBIDDEN", payload.Error);
        Assert.Equal(0, harness.OrderCount);
    }
}
