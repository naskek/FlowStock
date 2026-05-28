using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerFilledProductionPalletReservationRegressionTests
{
    private const long ItemId = 18;
    private const long LocationId = 1;
    private const long CustomerAId = 92;
    private const long CustomerALineId = 246;
    private const double OwnedHuQty = 378;
    private const double FreeHuQty = 300;
    private const string OwnedHu = "HU-0000652";
    private const string FreeHu = "HU-FREE-0001";

    [Fact]
    public void FilledCustomerProductionPallet_IsExcludedFromOtherCustomerCandidates_AndAutoBindKeepsFreeHuBehavior()
    {
        var scenario = CreateScenario(withFreeHu: true);
        var optimized = (IOptimizedHuReservationCandidatesStore)scenario.Harness.Store;

        var candidateRows = optimized.GetHuReservationCandidateSources(scenario.CustomerBId, [ItemId], Array.Empty<string>());

        Assert.DoesNotContain(candidateRows, row => string.Equals(row.HuCode, OwnedHu, StringComparison.OrdinalIgnoreCase));
        var freeCandidate = Assert.Single(candidateRows.Where(row =>
            string.Equals(row.HuCode, FreeHu, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(scenario.CustomerBId, freeCandidate.ReservedByOrderId);

        var createdPlan = scenario.Harness.GetOrderReceiptPlanLines(scenario.CustomerBId);
        Assert.DoesNotContain(createdPlan, line => string.Equals(line.ToHu, OwnedHu, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(createdPlan, line => string.Equals(line.ToHu, FreeHu, StringComparison.OrdinalIgnoreCase));

        scenario.OrderService.UpdateOrder(
            scenario.CustomerBId,
            orderRef: "101",
            partnerId: 501,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView
                {
                    ItemId = ItemId,
                    ItemName = "Горчица",
                    QtyOrdered = FreeHuQty
                }
            ],
            type: OrderType.Customer,
            bindReservedStockForCustomer: true);

        var updatedPlan = scenario.Harness.GetOrderReceiptPlanLines(scenario.CustomerBId);
        Assert.DoesNotContain(updatedPlan, line => string.Equals(line.ToHu, OwnedHu, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(updatedPlan, line => string.Equals(line.ToHu, FreeHu, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HuStockEndpoint_PrefersFilledProductionPalletOwner_OverConflictingPlanLine()
    {
        var scenario = CreateScenario(withFreeHu: false);
        scenario.Harness.SeedOrderReceiptPlanLines(scenario.CustomerBId, new OrderReceiptPlanLine
        {
            OrderId = scenario.CustomerBId,
            OrderLineId = scenario.CustomerBLineId,
            ItemId = ItemId,
            QtyPlanned = OwnedHuQty,
            ToHu = OwnedHu,
            SortOrder = 0
        });

        await using var host = await HuStockHost.StartAsync(scenario.Harness.Store);
        using var response = await host.Client.GetAsync("/api/hu-stock");

        Assert.True(response.IsSuccessStatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var ownedRow = document.RootElement.EnumerateArray().Single(row =>
            string.Equals(row.GetProperty("hu").GetString(), OwnedHu, StringComparison.OrdinalIgnoreCase)
            && row.GetProperty("item_id").GetInt64() == ItemId);

        Assert.Equal(CustomerAId, ownedRow.GetProperty("reserved_customer_order_id").GetInt64());
        Assert.Equal("092", ownedRow.GetProperty("reserved_customer_order_ref").GetString());
    }

    [Fact]
    public void ExplicitApply_ToOtherCustomerFilledProductionPallet_FailsWithReservedByOtherOrder()
    {
        var scenario = CreateScenario(withFreeHu: false);

        var ex = Assert.Throws<OrderHuReservationApplyException>(() => scenario.ApplyService.Apply(
            scenario.CustomerBId,
            new OrderHuReservationApplyRequest
            {
                Lines =
                [
                    new OrderHuReservationApplyLineRequest
                    {
                        OrderLineId = scenario.CustomerBLineId,
                        SelectedHuCodes = [OwnedHu]
                    }
                ]
            }));

        Assert.Equal("HU_RESERVED_BY_OTHER_ORDER", ex.ErrorCode);
    }

    private static RegressionScenario CreateScenario(bool withFreeHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = LocationId,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 500,
            Code = "CUST-A",
            Name = "Клиент A"
        });
        harness.SeedPartner(new Partner
        {
            Id = 501,
            Code = "CUST-B",
            Name = "Клиент B"
        });
        harness.SeedItemType(new ItemType
        {
            Id = 60,
            Name = "Готовая продукция",
            EnableOrderReservation = true
        });
        harness.SeedItem(new Item
        {
            Id = ItemId,
            Name = "Горчица",
            ItemTypeId = 60,
            ItemTypeName = "Готовая продукция",
            MaxQtyPerHu = OwnedHuQty
        });
        harness.SeedOrder(new Order
        {
            Id = CustomerAId,
            OrderRef = "092",
            Type = OrderType.Customer,
            PartnerId = 500,
            PartnerName = "Клиент A",
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 28, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = CustomerALineId,
            OrderId = CustomerAId,
            ItemId = ItemId,
            QtyOrdered = OwnedHuQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        harness.SeedDoc(new Doc
        {
            Id = 301,
            DocRef = "PRD-2026-000301",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = CustomerAId,
            OrderRef = "092",
            CreatedAt = new DateTime(2026, 5, 28, 10, 0, 0),
            ClosedAt = new DateTime(2026, 5, 28, 10, 30, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 30101,
            DocId = 301,
            OrderLineId = CustomerALineId,
            ItemId = ItemId,
            Qty = OwnedHuQty,
            ToLocationId = LocationId,
            ToHu = OwnedHu
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 652,
            PrdDocId = 301,
            DocLineId = 30101,
            OrderId = CustomerAId,
            OrderLineId = CustomerALineId,
            ItemId = ItemId,
            HuCode = OwnedHu,
            PlannedQty = OwnedHuQty,
            ToLocationId = LocationId,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 28, 10, 20, 0)
        });
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            Timestamp = new DateTime(2026, 5, 28, 10, 30, 0),
            DocId = 301,
            ItemId = ItemId,
            LocationId = LocationId,
            QtyDelta = OwnedHuQty,
            HuCode = OwnedHu
        });

        if (withFreeHu)
        {
            harness.SeedBalance(ItemId, LocationId, FreeHuQty, FreeHu);
        }

        var orderService = new OrderService(harness.Store);
        var createdOrderId = orderService.CreateOrder(
            orderRef: "101",
            partnerId: 501,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView
                {
                    ItemId = ItemId,
                    ItemName = "Горчица",
                    QtyOrdered = FreeHuQty
                }
            ],
            type: OrderType.Customer,
            bindReservedStockForCustomer: true);

        var customerBLineId = Assert.Single(harness.Store.GetOrderLines(createdOrderId)).Id;
        return new RegressionScenario(
            harness,
            orderService,
            new OrderHuReservationApplyService(harness.Store),
            createdOrderId,
            customerBLineId);
    }

    private sealed record RegressionScenario(
        CloseDocumentHarness Harness,
        OrderService OrderService,
        OrderHuReservationApplyService ApplyService,
        long CustomerBId,
        long CustomerBLineId);

    private sealed class HuStockHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private HuStockHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<HuStockHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(HuStockReadModelMapper).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);

            var app = builder.Build();
            app.MapGet("/api/hu-stock", (IDataStore dataStore) =>
            {
                var contextByKey = HuStockReadModelMapper.BuildContextMap(dataStore.GetHuOrderContextRows());
                var rows = dataStore.GetHuStockRows()
                    .Select(row => HuStockReadModelMapper.Map(row.ItemId, row.LocationId, row.HuCode, row.Qty, contextByKey))
                    .ToList();
                return Results.Ok(rows);
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()?
                .Addresses
                .Single();

            return new HuStockHost(app, new HttpClient
            {
                BaseAddress = new Uri(address!, UriKind.Absolute)
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
