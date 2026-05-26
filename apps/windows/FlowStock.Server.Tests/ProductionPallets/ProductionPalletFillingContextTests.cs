using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletFillingContextTests
{
    [Fact]
    public void GetFillingContext_ExcludesCancelledPallets_FromDocumentPallets()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var context = service.GetFillingContext(fixture.OrderId);

        Assert.DoesNotContain(
            context.Document.Pallets,
            pallet => string.Equals(pallet.HuCode, fixture.CancelledHuCodes[0], StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            context.Document.Pallets,
            pallet => string.Equals(pallet.HuCode, fixture.CancelledHuCodes[1], StringComparison.OrdinalIgnoreCase));
        Assert.Equal(8, context.Document.Pallets.Count);
    }

    [Fact]
    public void GetFillingContext_Summary_DoesNotCountCancelledPallets()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var context = service.GetFillingContext(fixture.OrderId);

        Assert.Equal(8, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(4800, context.Document.Summary.PlannedQty, 3);
        Assert.Equal(8, context.Document.Summary.FilledPalletCount);
        Assert.Equal(4800, context.Document.Summary.FilledQty, 3);
        Assert.Equal(0, context.Document.Summary.RemainingPalletCount);
        Assert.Equal(0, context.Document.Summary.RemainingQty, 3);
    }

    [Fact]
    public void GetFillingContext_LineSummary_RemainingPalletCount_ExcludesCancelledPallets()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var context = service.GetFillingContext(fixture.OrderId);
        var line = Assert.Single(context.Document.Lines, row => row.OrderLineId == fixture.OrderLineId);

        Assert.Equal(8, line.FilledPalletCount);
        Assert.Equal(4800, line.FilledQty, 3);
        Assert.Equal(0, line.RemainingPalletCount);
        Assert.Equal(0, line.RemainingQty, 3);
    }

    [Fact]
    public void Scan_CancelledHu_ReturnsClearRejection()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var result = service.Scan(fixture.OrderId, fixture.PrdDocId, fixture.CancelledHuCodes[0]);

        Assert.False(result.Success);
        Assert.Equal("Паллета отменена и не может быть наполнена.", result.Error);
    }

    [Fact]
    public void Fill_CancelledHu_ReturnsClearRejection()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var result = service.Fill(fixture.CancelledHuCodes[0], "TSD-01", fixture.OrderId, fixture.PrdDocId);

        Assert.False(result.Success);
        Assert.Equal("Паллета отменена и не может быть наполнена.", result.Error);
    }

    [Fact]
    public void GetFillingOrders_DoesNotListFullyFilledOrder_WithOnlyCancelledLeftover()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);

        Assert.Empty(service.GetFillingOrders());
    }

    [Fact]
    public void Scan_FilledHu_ReturnsAlreadyFilled_AndNotPending()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        var service = new ProductionPalletService(fixture.Harness.Store);
        var filledHu = fixture.Harness.Store.GetProductionPalletsByDoc(fixture.PrdDocId)
            .First(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            .HuCode;

        var result = service.Scan(fixture.OrderId, fixture.PrdDocId, filledHu);

        Assert.True(result.Success);
        Assert.True(result.AlreadyFilled);
        Assert.Equal(0, result.Document?.Summary.RemainingPalletCount);
    }

    [Fact]
    public async Task FillingContextHttp_ExcludesCancelledPallets()
    {
        var fixture = CreateFilledOrderWithCancelledPallets();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(fixture.Harness);

        var response = await host.Client.GetAsync($"/api/tsd/production/orders/{fixture.OrderId}/filling-context");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(ContainsHu(json, fixture.CancelledHuCodes[0]));
        Assert.False(ContainsHu(json, fixture.CancelledHuCodes[1]));
        var summary = root.GetProperty("document").GetProperty("summary");
        Assert.Equal(8, summary.GetProperty("planned_pallet_count").GetInt32());
        Assert.Equal(8, summary.GetProperty("filled_pallet_count").GetInt32());
        Assert.Equal(0, summary.GetProperty("remaining_pallet_count").GetInt32());
        Assert.Equal(0, summary.GetProperty("remaining_qty").GetDouble(), 3);
    }

    private static bool ContainsHu(string json, string huCode)
    {
        return json.Contains(huCode, StringComparison.OrdinalIgnoreCase);
    }

    private static FilledOrderWithCancelledFixture CreateFilledOrderWithCancelledPallets()
    {
        const long orderId = 72;
        const long orderLineId = 189;
        const long itemId = 7200;
        const long prdDocId = 7200;
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = "Горчица, Печагин, 1 кг",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = "072",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 4800,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = "PRD-2026-000072",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = "072",
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });

        var nextPalletId = 1L;
        var nextDocLineId = 72001L;
        for (var i = 0; i < 8; i++)
        {
            var huCode = $"HU-FILLED-{i + 1:000}";
            harness.SeedLine(new DocLine
            {
                Id = nextDocLineId++,
                DocId = prdDocId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                Qty = 600,
                ToLocationId = 1,
                ToHu = huCode,
                PackSingleHu = true
            });
            harness.SeedProductionPallet(BuildPallet(nextPalletId++, huCode, ProductionPalletStatus.Filled, nextDocLineId - 1, orderId, orderLineId, itemId));
        }

        var cancelledHuCodes = new[] { "HU-0000476", "HU-0000477" };
        foreach (var huCode in cancelledHuCodes)
        {
            harness.SeedLine(new DocLine
            {
                Id = nextDocLineId++,
                DocId = prdDocId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                Qty = 600,
                ToLocationId = 1,
                ToHu = huCode,
                PackSingleHu = true
            });
            harness.SeedProductionPallet(BuildPallet(
                nextPalletId++,
                huCode,
                ProductionPalletStatus.Cancelled,
                nextDocLineId - 1,
                orderId,
                orderLineId,
                itemId));
        }

        return new FilledOrderWithCancelledFixture(harness, orderId, orderLineId, prdDocId, cancelledHuCodes);
    }

    private static ProductionPallet BuildPallet(
        long id,
        string huCode,
        string status,
        long docLineId,
        long orderId,
        long orderLineId,
        long itemId)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = 7200,
            DocLineId = docLineId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = "Горчица, Печагин, 1 кг",
            HuCode = huCode,
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = id * 10,
                    ProductionPalletId = id,
                    DocLineId = docLineId,
                    OrderLineId = orderLineId,
                    ItemId = itemId,
                    ItemName = "Горчица, Печагин, 1 кг",
                    PlannedQty = 600,
                    FilledQty = status == ProductionPalletStatus.Filled ? 600 : 0,
                    CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
                }
            ]
        };
    }

    private sealed record FilledOrderWithCancelledFixture(
        CloseDocumentHarness Harness,
        long OrderId,
        long OrderLineId,
        long PrdDocId,
        IReadOnlyList<string> CancelledHuCodes);
}

internal sealed class ProductionPalletTsdHttpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ProductionPalletTsdHttpHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ProductionPalletTsdHttpHost> StartAsync(CloseDocumentHarness harness)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProductionPalletEndpoints).Assembly.FullName,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDataStore>(harness.Store);
        builder.Services.AddSingleton(sp => new ProductionPalletService(sp.GetRequiredService<IDataStore>()));
        var app = builder.Build();
        ProductionPalletEndpoints.Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            .SingleOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("HTTP test host did not expose a listening address.");
        }

        return new ProductionPalletTsdHttpHost(app, new HttpClient { BaseAddress = new Uri(address, UriKind.Absolute) });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
