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

        Assert.Contains(service.GetFillingOrders(), order => order.OrderId == 72 && order.Progress.CanClose);
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

    [Fact]
    public void GetFillingContext_ExcludesActiveOrphanHeaderPallets()
    {
        var fixture = CreateOrderWithValidAndOrphanPallet();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var context = service.GetFillingContext(fixture.OrderId);

        var pallet = Assert.Single(context.Document.Pallets);
        Assert.Equal(fixture.ValidHuCode, pallet.HuCode);
        Assert.Equal(fixture.ValidOrderLineId, pallet.OrderLineId);
        Assert.Equal(1, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(600, context.Document.Summary.PlannedQty, 3);
        Assert.DoesNotContain(context.Document.Pallets, row => row.OrderLineId == null);
        Assert.DoesNotContain(context.Document.Pallets, row => string.Equals(row.HuCode, fixture.OrphanHuCode, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Document.Lines, line => line.OrderLineId == null);
        Assert.DoesNotContain(context.Document.Lines, line => line.ItemId == fixture.OrphanItemId);
    }

    [Fact]
    public void BuildOrderOwnedPalletSummary_ExcludesActiveOrphanHeaderPallets_AndMatchesFillingContext()
    {
        var fixture = CreateOrderWithValidAndOrphanPallet();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var summary = ProductionPalletService.BuildOrderOwnedPalletSummary(fixture.Harness.Store, fixture.OrderId);
        var contextSummary = service.GetFillingContext(fixture.OrderId).Document.Summary;

        Assert.Equal(1, summary.PlannedPalletCount);
        Assert.Equal(0, summary.FilledPalletCount);
        Assert.Equal(600, summary.PlannedQty, 3);
        Assert.Equal(600, summary.RemainingQty, 3);
        Assert.Equal(contextSummary.PlannedPalletCount, summary.PlannedPalletCount);
        Assert.Equal(contextSummary.FilledPalletCount, summary.FilledPalletCount);
        Assert.Equal(contextSummary.PlannedQty, summary.PlannedQty, 3);
    }

    [Fact]
    public async Task FillingContextHttp_ExcludesActiveOrphanHeaderPallets()
    {
        var fixture = CreateOrderWithValidAndOrphanPallet();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(fixture.Harness);

        var response = await host.Client.GetAsync($"/api/tsd/production/orders/{fixture.OrderId}/filling-context");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(ContainsHu(json, fixture.ValidHuCode));
        Assert.False(ContainsHu(json, fixture.OrphanHuCode));
        var summary = root.GetProperty("document").GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("planned_pallet_count").GetInt32());
        Assert.Equal(600, summary.GetProperty("planned_qty").GetDouble(), 3);
        Assert.All(root.GetProperty("document").GetProperty("lines").EnumerateArray(), line =>
            Assert.True(line.GetProperty("order_line_id").ValueKind != JsonValueKind.Null));
    }

    [Fact]
    public void ScanAndFill_ActiveOrphanHeaderPallet_ReturnBusinessErrorWithoutFilling()
    {
        var fixture = CreateOrderWithValidAndOrphanPallet();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var scan = service.Scan(fixture.OrderId, fixture.PrdDocId, fixture.OrphanHuCode);
        var fill = service.Fill(fixture.OrphanHuCode, "TSD-01", fixture.OrderId, fixture.PrdDocId);

        Assert.False(scan.Success);
        Assert.Equal("Строка заказа для паллеты не найдена.", scan.Error);
        Assert.False(fill.Success);
        Assert.Equal("Строка заказа для паллеты не найдена.", fill.Error);
        Assert.Equal(
            ProductionPalletStatus.Planned,
            fixture.Harness.Store.GetProductionPalletByHu(fixture.OrphanHuCode)?.Status);
        Assert.Empty(fixture.Harness.LedgerEntries);
    }

    [Fact]
    public void GetFillingContext_MixedPallets_KeepOnlyValidComponentsAndExcludeFullyOrphanMixed()
    {
        var fixture = CreateOrderWithMixedOrphanComponents();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var context = service.GetFillingContext(fixture.OrderId);

        var pallet = Assert.Single(context.Document.Pallets);
        Assert.Equal(fixture.PartialMixedHuCode, pallet.HuCode);
        var line = Assert.Single(pallet.Lines);
        Assert.Equal(fixture.ValidOrderLineId, line.OrderLineId);
        Assert.Equal(fixture.ValidItemId, line.ItemId);
        Assert.Equal(600, pallet.PlannedQty, 3);
        Assert.Equal(1, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(600, context.Document.Summary.PlannedQty, 3);
        Assert.DoesNotContain(context.Document.Pallets, row => string.Equals(row.HuCode, fixture.FullyOrphanMixedHuCode, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Document.Lines, row => row.OrderLineId == null);
        Assert.DoesNotContain(context.Document.Lines, row => row.ItemId == fixture.OrphanItemId);
    }

    [Fact]
    public void BuildOrderOwnedPalletSummary_MixedPallets_CountsOnlyValidComponents()
    {
        var fixture = CreateOrderWithMixedOrphanComponents();

        var summary = ProductionPalletService.BuildOrderOwnedPalletSummary(fixture.Harness.Store, fixture.OrderId);

        Assert.Equal(1, summary.PlannedPalletCount);
        Assert.Equal(0, summary.FilledPalletCount);
        Assert.Equal(600, summary.PlannedQty, 3);
        Assert.Equal(600, summary.RemainingQty, 3);
    }

    [Fact]
    public void ScanAndFill_PartiallyOrphanMixedPallet_ReturnBusinessErrorWithoutFilling()
    {
        var fixture = CreateOrderWithMixedOrphanComponents();
        var service = new ProductionPalletService(fixture.Harness.Store);

        var scan = service.Scan(fixture.OrderId, fixture.PrdDocId, fixture.PartialMixedHuCode);
        var fill = service.Fill(fixture.PartialMixedHuCode, "TSD-01", fixture.OrderId, fixture.PrdDocId);

        Assert.False(scan.Success);
        Assert.Equal("Строка заказа для паллеты не найдена.", scan.Error);
        Assert.False(fill.Success);
        Assert.Equal("Строка заказа для паллеты не найдена.", fill.Error);
        var pallet = fixture.Harness.Store.GetProductionPalletByHu(fixture.PartialMixedHuCode);
        Assert.NotNull(pallet);
        Assert.Equal(ProductionPalletStatus.Planned, pallet.Status);
        Assert.All(pallet.Lines, line => Assert.Equal(0, line.FilledQty, 3));
        Assert.Empty(fixture.Harness.LedgerEntries);
    }

    private static bool ContainsHu(string json, string huCode)
    {
        return json.Contains(huCode, StringComparison.OrdinalIgnoreCase);
    }

    private static OrphanFillingFixture CreateOrderWithValidAndOrphanPallet()
    {
        const long orderId = 150;
        const long validOrderLineId = 334;
        const long validItemId = 6;
        const long orphanItemId = 23;
        const long prdDocId = 1500;
        const string validHuCode = "HU-0000881";
        const string orphanHuCode = "HU-0000885";
        var harness = CreateBaseFillingHarness(orderId, validOrderLineId, validItemId, orphanItemId, prdDocId);

        harness.SeedLine(BuildDocLine(15001, prdDocId, validOrderLineId, validItemId, 600, validHuCode));
        harness.SeedProductionPallet(BuildPalletForFilling(
            id: 1,
            prdDocId,
            docLineId: 15001,
            orderId,
            orderLineId: validOrderLineId,
            itemId: validItemId,
            itemName: "Горчица",
            huCode: validHuCode,
            plannedQty: 600,
            lines:
            [
                BuildComponentLine(11, 1, 15001, validOrderLineId, validItemId, "Горчица", 600)
            ]));

        harness.SeedLine(BuildDocLine(15002, prdDocId, null, orphanItemId, 1800, orphanHuCode));
        harness.SeedProductionPallet(BuildPalletForFilling(
            id: 2,
            prdDocId,
            docLineId: 15002,
            orderId,
            orderLineId: null,
            itemId: orphanItemId,
            itemName: "Аджика",
            huCode: orphanHuCode,
            plannedQty: 1800));

        return new OrphanFillingFixture(
            harness,
            orderId,
            prdDocId,
            validOrderLineId,
            validItemId,
            orphanItemId,
            validHuCode,
            orphanHuCode);
    }

    private static MixedOrphanFillingFixture CreateOrderWithMixedOrphanComponents()
    {
        const long orderId = 151;
        const long validOrderLineId = 335;
        const long validItemId = 6;
        const long orphanItemId = 23;
        const long prdDocId = 1510;
        const string partialMixedHuCode = "HU-MIX-PARTIAL";
        const string fullyOrphanMixedHuCode = "HU-MIX-ORPHAN";
        var harness = CreateBaseFillingHarness(orderId, validOrderLineId, validItemId, orphanItemId, prdDocId);

        harness.SeedLine(BuildDocLine(15101, prdDocId, validOrderLineId, validItemId, 600, partialMixedHuCode));
        harness.SeedLine(BuildDocLine(15102, prdDocId, null, orphanItemId, 600, partialMixedHuCode));
        harness.SeedProductionPallet(BuildPalletForFilling(
            id: 10,
            prdDocId,
            docLineId: 15101,
            orderId,
            orderLineId: null,
            itemId: validItemId,
            itemName: "Микс-паллета",
            huCode: partialMixedHuCode,
            plannedQty: 1200,
            lines:
            [
                BuildComponentLine(101, 10, 15101, validOrderLineId, validItemId, "Горчица", 600),
                BuildComponentLine(102, 10, 15102, null, orphanItemId, "Аджика", 600)
            ]));

        harness.SeedLine(BuildDocLine(15103, prdDocId, null, orphanItemId, 600, fullyOrphanMixedHuCode));
        harness.SeedLine(BuildDocLine(15104, prdDocId, null, orphanItemId, 600, fullyOrphanMixedHuCode));
        harness.SeedProductionPallet(BuildPalletForFilling(
            id: 11,
            prdDocId,
            docLineId: 15103,
            orderId,
            orderLineId: null,
            itemId: orphanItemId,
            itemName: "Микс-паллета",
            huCode: fullyOrphanMixedHuCode,
            plannedQty: 1200,
            lines:
            [
                BuildComponentLine(111, 11, 15103, null, orphanItemId, "Аджика", 600),
                BuildComponentLine(112, 11, 15104, null, orphanItemId, "Аджика", 600)
            ]));

        return new MixedOrphanFillingFixture(
            harness,
            orderId,
            prdDocId,
            validOrderLineId,
            validItemId,
            orphanItemId,
            partialMixedHuCode,
            fullyOrphanMixedHuCode);
    }

    private static CloseDocumentHarness CreateBaseFillingHarness(
        long orderId,
        long validOrderLineId,
        long validItemId,
        long orphanItemId,
        long prdDocId)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = validItemId, Name = "Горчица", Brand = "Печагин", BaseUom = "шт", MaxQtyPerHu = 600 });
        harness.SeedItem(new Item { Id = orphanItemId, Name = "Аджика", Brand = "Печагин", BaseUom = "шт", MaxQtyPerHu = 1800 });
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderId.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 6, 4, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = validOrderLineId,
            OrderId = orderId,
            ItemId = validItemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = $"PRD-2026-{orderId:000000}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderId.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            CreatedAt = new DateTime(2026, 6, 4, 10, 0, 0)
        });

        return harness;
    }

    private static DocLine BuildDocLine(
        long id,
        long prdDocId,
        long? orderLineId,
        long itemId,
        double qty,
        string huCode)
    {
        return new DocLine
        {
            Id = id,
            DocId = prdDocId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = itemId,
            Qty = qty,
            ToLocationId = 1,
            ToHu = huCode,
            PackSingleHu = true
        };
    }

    private static ProductionPalletComponentLine BuildComponentLine(
        long id,
        long productionPalletId,
        long docLineId,
        long? orderLineId,
        long itemId,
        string itemName,
        double plannedQty)
    {
        return new ProductionPalletComponentLine
        {
            Id = id,
            ProductionPalletId = productionPalletId,
            DocLineId = docLineId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = itemName,
            Brand = "Печагин",
            Uom = "шт",
            PlannedQty = plannedQty,
            FilledQty = 0,
            CreatedAt = new DateTime(2026, 6, 4, 10, 0, 0)
        };
    }

    private static ProductionPallet BuildPalletForFilling(
        long id,
        long prdDocId,
        long docLineId,
        long orderId,
        long? orderLineId,
        long itemId,
        string itemName,
        string huCode,
        double plannedQty,
        IReadOnlyList<ProductionPalletComponentLine>? lines = null)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = prdDocId,
            DocLineId = docLineId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = itemName,
            HuCode = huCode,
            PlannedQty = plannedQty,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 6, 4, 10, 0, 0),
            Lines = lines ?? Array.Empty<ProductionPalletComponentLine>()
        };
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

    private sealed record OrphanFillingFixture(
        CloseDocumentHarness Harness,
        long OrderId,
        long PrdDocId,
        long ValidOrderLineId,
        long ValidItemId,
        long OrphanItemId,
        string ValidHuCode,
        string OrphanHuCode);

    private sealed record MixedOrphanFillingFixture(
        CloseDocumentHarness Harness,
        long OrderId,
        long PrdDocId,
        long ValidOrderLineId,
        long ValidItemId,
        long OrphanItemId,
        string PartialMixedHuCode,
        string FullyOrphanMixedHuCode);
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

    public static async Task<ProductionPalletTsdHttpHost> StartAsync(
        CloseDocumentHarness harness,
        ProductionPalletService? service = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProductionPalletEndpoints).Assembly.FullName,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDataStore>(harness.Store);
        if (service == null)
        {
            builder.Services.AddSingleton(sp => new ProductionPalletService(sp.GetRequiredService<IDataStore>()));
        }
        else
        {
            builder.Services.AddSingleton(service);
        }
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
