using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection("UpdateOrder")]
public sealed class CanonicalUpdateIntegrationTests
{
    [Fact]
    public async Task Quantity_only_update_of_legacy_null_price_line_preserves_null_and_vat()
    {
        var (harness, apiStore, orderId) =
            UpdateOrderHttpScenario.CreateCustomerScenario(firstLineUnitPriceGross: null);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "001",
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        OrderLineId = 101,
                        ItemId = 1001,
                        QtyOrdered = 12
                    },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        OrderLineId = 102,
                        ItemId = 1002,
                        QtyOrdered = 5
                    }
                ]
            });

        var line = Assert.Single(harness.GetOrderLines(orderId), row => row.Id == 101);
        Assert.Equal(12, line.QtyOrdered);
        Assert.Null(line.UnitPriceGross);
        Assert.Equal(22m, line.VatRate);
    }

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
                PartnerId = 200,
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
        Assert.Equal(200, order.PartnerId);
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

    [Fact]
    public async Task Quantity_only_update_preserves_price_and_vat_snapshots()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "001",
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        OrderLineId = 101,
                        ItemId = 1001,
                        QtyOrdered = 12
                    },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        OrderLineId = 102,
                        ItemId = 1002,
                        QtyOrdered = 5
                    }
                ]
            });

        var line = Assert.Single(harness.GetOrderLines(orderId), row => row.Id == 101);
        Assert.Equal(100m, line.UnitPriceGross);
        Assert.Equal(22m, line.VatRate);
    }

    [Fact]
    public async Task Explicit_price_change_is_allowed_before_shipment_and_vat_stays_unchanged()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            new UpdateOrderHttpApi.UpdateOrderRequest
            {
                OrderRef = "001",
                Type = "CUSTOMER",
                PartnerId = 200,
                Lines =
                [
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        OrderLineId = 101,
                        ItemId = 1001,
                        QtyOrdered = 10,
                        ChangeUnitPriceGross = true,
                        UnitPriceGross = 135.25m
                    },
                    new UpdateOrderHttpApi.UpdateOrderLineRequest
                    {
                        OrderLineId = 102,
                        ItemId = 1002,
                        QtyOrdered = 5
                    }
                ]
            });

        var line = Assert.Single(harness.GetOrderLines(orderId), row => row.Id == 101);
        Assert.Equal(135.25m, line.UnitPriceGross);
        Assert.Equal(22m, line.VatRate);
    }

    [Fact]
    public async Task Shipment_blocks_actual_price_change_but_same_value_is_noop()
    {
        var (harness, apiStore, orderId) = UpdateOrderHttpScenario.CreateCustomerScenario();
        harness.SeedCommercialShipment(101);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var rejected = await host.Client.PutAsJsonAsync(
            $"/api/orders/{orderId}",
            BuildPriceUpdate(101m));
        var error = await UpdateOrderHttpApi.ReadApiErrorResultAsync(rejected, HttpStatusCode.BadRequest);
        Assert.Equal("ORDER_LINE_PRICE_LOCKED_BY_SHIPMENT", error.Error);

        var accepted = await UpdateOrderHttpApi.UpdateAsync(
            host.Client,
            orderId,
            BuildPriceUpdate(100m));
        Assert.True(accepted.Ok);
        Assert.Equal(100m, harness.GetOrderLines(orderId).Single(row => row.Id == 101).UnitPriceGross);
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest BuildPriceUpdate(decimal price) => new()
    {
        OrderRef = "001",
        Type = "CUSTOMER",
        PartnerId = 200,
        Lines =
        [
            new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 101,
                ItemId = 1001,
                QtyOrdered = 10,
                ChangeUnitPriceGross = true,
                UnitPriceGross = price
            },
            new UpdateOrderHttpApi.UpdateOrderLineRequest
            {
                OrderLineId = 102,
                ItemId = 1002,
                QtyOrdered = 5
            }
        ]
    };
}
