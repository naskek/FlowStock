using System.Net;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletSafeOnlyPlanTests
{
    [Fact]
    public void SafeOnly_PlansOnlyUnaffectedLines()
    {
        var harness = CreateTwoLineHarness();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.SkipInternalSupply);

        Assert.True(result.ProductionRequired);
        Assert.Equal(500, result.Summary.PlannedQty);
        var pallets = GetActivePallets(harness, 10);
        Assert.Single(pallets);
        Assert.Equal(102, pallets[0].OrderLineId);
        Assert.Equal([102L], result.PlannedOrderLineIds);
        var skipped = Assert.Single(result.SkippedLines);
        Assert.Equal(101, skipped.OrderLineId);
        Assert.Equal("Аджика", skipped.ItemName);
        Assert.Equal(ProductionPalletPlanSkippedReason.ExpectedInternalSupply, skipped.SkippedReason);
        var internalRef = Assert.Single(skipped.InternalRefs);
        Assert.Equal("104", internalRef.InternalOrderRef);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void SafeOnly_AllLinesAffected_NoOpWithoutWrites()
    {
        var harness = CreateTwoLineHarness(internalCoversBothItems: true);
        var docsBefore = harness.Store.GetDocsByOrder(10).Count;
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.SkipInternalSupply);

        Assert.False(result.ProductionRequired);
        Assert.Equal("Все позиции пересекаются с ожидаемым внутренним выпуском. План не создан.", result.Message);
        Assert.Equal(2, result.SkippedLines.Count);
        Assert.Empty(result.PlannedOrderLineIds);
        Assert.Equal(docsBefore, harness.Store.GetDocsByOrder(10).Count);
        Assert.Empty(GetActivePallets(harness, 10));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void SafeOnly_MixedGroupPartiallyAffected_SkipsWholeGroupAtomically()
    {
        var harness = CreateTwoLineHarness(mixedGroup: "1");
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10, ProductionPalletPlanMode.SkipInternalSupply);

        Assert.False(result.ProductionRequired);
        Assert.Empty(GetActivePallets(harness, 10));
        Assert.Equal(2, result.SkippedLines.Count);
        var direct = Assert.Single(result.SkippedLines, line => line.OrderLineId == 101);
        Assert.Equal(ProductionPalletPlanSkippedReason.ExpectedInternalSupply, direct.SkippedReason);
        Assert.Equal("1", direct.ProductionPalletGroup);
        var propagated = Assert.Single(result.SkippedLines, line => line.OrderLineId == 102);
        Assert.Equal(ProductionPalletPlanSkippedReason.MixedGroupContainsExpectedInternalSupply, propagated.SkippedReason);
        Assert.Equal(101, propagated.TriggeredByOrderLineId);
        Assert.Empty(propagated.InternalRefs);
    }

    [Fact]
    public void SafeOnly_Idempotent()
    {
        var harness = CreateTwoLineHarness();
        var service = new ProductionPalletService(harness.Store);

        var first = service.PlanOrder(10, ProductionPalletPlanMode.SkipInternalSupply);
        var palletsAfterFirst = GetActivePallets(harness, 10);
        var second = service.PlanOrder(10, ProductionPalletPlanMode.SkipInternalSupply);
        var palletsAfterSecond = GetActivePallets(harness, 10);

        Assert.True(first.ProductionRequired);
        Assert.Single(palletsAfterFirst);
        Assert.Equal(palletsAfterFirst.Select(pallet => pallet.Id), palletsAfterSecond.Select(pallet => pallet.Id));
        Assert.False(second.ProductionRequired);
        Assert.Single(second.SkippedLines);
    }

    [Fact]
    public void SafeOnly_RecomputesInsideCommand()
    {
        var harness = CreateTwoLineHarness();
        var service = new ProductionPalletService(harness.Store);

        // Preview показал warning, но до команды INTERNAL-заказ закрыли.
        var previewBefore = service.GetCustomerPrePlanCoveragePreview(10);
        Assert.True(previewBefore.HasWarning);
        harness.SeedOrder(new Order
        {
            Id = 30,
            OrderRef = "104",
            Type = OrderType.Internal,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 7, 1, 9, 0, 0)
        });

        var result = service.PlanOrder(10, ProductionPalletPlanMode.SkipInternalSupply);

        Assert.True(result.ProductionRequired);
        Assert.Empty(result.SkippedLines);
        Assert.Equal(878, result.Summary.PlannedQty);
        Assert.Equal(2, GetActivePallets(harness, 10).Select(pallet => pallet.OrderLineId).Distinct().Count());
    }

    [Fact]
    public void SafeOnly_ForInternalOrder_Throws()
    {
        var harness = CreateTwoLineHarness();
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(
            () => service.PlanOrder(30, ProductionPalletPlanMode.SkipInternalSupply));
        Assert.Contains("только для клиентского заказа", ex.Message, StringComparison.Ordinal);
        Assert.Empty(GetActivePallets(harness, 30));
    }

    [Fact]
    public void FullPlan_WithoutMode_PreservesBusinessBehavior()
    {
        var modeHarness = CreateTwoLineHarness();
        var modeResult = new ProductionPalletService(modeHarness.Store).PlanOrder(10, ProductionPalletPlanMode.Full);

        var legacyHarness = CreateTwoLineHarness();
        var legacyResult = new ProductionPalletService(legacyHarness.Store).PlanOrder(10);

        Assert.Equal(legacyResult.Summary.PlannedQty, modeResult.Summary.PlannedQty);
        Assert.Equal(legacyResult.Summary.PlannedPalletCount, modeResult.Summary.PlannedPalletCount);
        Assert.Equal(legacyResult.ProductionRequired, modeResult.ProductionRequired);
        Assert.Equal(878, modeResult.Summary.PlannedQty);
        Assert.Empty(modeResult.SkippedLines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"foo\":\"bar\"}")]
    [InlineData("{\"mode\":\"full\"}")]
    public async Task Api_PlanWithoutSkipMode_PreservesExistingBehavior(string? body)
    {
        var harness = CreateTwoLineHarness();

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var content = body == null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/api/orders/10/production-pallets/plan", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(responseBody);
        Assert.True(json.RootElement.GetProperty("production_required").GetBoolean());
        Assert.Equal("full", json.RootElement.GetProperty("mode").GetString());
        Assert.Empty(json.RootElement.GetProperty("skipped_lines").EnumerateArray());
        Assert.Equal(878, json.RootElement.GetProperty("planned_qty").GetDouble());
        Assert.Equal(2, GetActivePallets(harness, 10).Select(pallet => pallet.OrderLineId).Distinct().Count());
    }

    [Fact]
    public async Task Api_PlanWithSkipMode_ReturnsServerActualSummary()
    {
        var harness = CreateTwoLineHarness();

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var content = new StringContent("{\"mode\":\"skip_internal_supply\"}", Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/api/orders/10/production-pallets/plan", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(responseBody);
        Assert.Equal("skip_internal_supply", json.RootElement.GetProperty("mode").GetString());
        Assert.True(json.RootElement.GetProperty("production_required").GetBoolean());
        var plannedLineId = Assert.Single(json.RootElement.GetProperty("planned_order_line_ids").EnumerateArray());
        Assert.Equal(102, plannedLineId.GetInt64());
        var skipped = Assert.Single(json.RootElement.GetProperty("skipped_lines").EnumerateArray());
        Assert.Equal(101, skipped.GetProperty("customer_order_line_id").GetInt64());
        Assert.Equal("expected_internal_supply", skipped.GetProperty("skipped_reason").GetString());
        Assert.True(skipped.GetProperty("triggered_by_order_line_id").ValueKind == JsonValueKind.Null);
        var internalRef = Assert.Single(skipped.GetProperty("internal_refs").EnumerateArray());
        Assert.Equal("104", internalRef.GetProperty("internal_order_ref").GetString());
        var pallets = GetActivePallets(harness, 10);
        Assert.Single(pallets);
        Assert.Equal(102, pallets[0].OrderLineId);
    }

    [Theory]
    [InlineData("{\"mode\":\"x\"}", "INVALID_PLAN_MODE")]
    [InlineData("not-json", "INVALID_JSON")]
    public async Task Api_PlanWithInvalidBody_Returns400WithoutWrites(string body, string expectedError)
    {
        var harness = CreateTwoLineHarness();

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/api/orders/10/production-pallets/plan", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(responseBody);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedError, json.RootElement.GetProperty("error").GetString());
        Assert.Empty(GetActivePallets(harness, 10));
    }

    [Fact]
    public async Task Api_PlanInternalOrderWithSkipMode_Returns400()
    {
        var harness = CreateTwoLineHarness();

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var content = new StringContent("{\"mode\":\"skip_internal_supply\"}", Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/api/orders/30/production-pallets/plan", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(GetActivePallets(harness, 30));
    }

    private static IReadOnlyList<ProductionPallet> GetActivePallets(CloseDocumentHarness harness, long orderId)
    {
        return harness.Store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    /// <summary>
    /// CUSTOMER-заказ 10: строка 101 (Аджика, item 100, 378) и строка 102 (Хрен, item 300, 500).
    /// INTERNAL-заказ 30 «104» ожидает item 100 (и опционально item 300).
    /// </summary>
    private static CloseDocumentHarness CreateTwoLineHarness(
        bool internalCoversBothItems = false,
        string? mixedGroup = null)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Аджика",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 300,
            Name = "Хрен",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "086",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerName = "Клиент",
            CreatedAt = new DateTime(2026, 7, 1, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 378,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = mixedGroup
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 300,
            QtyOrdered = 500,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = mixedGroup
        });

        harness.SeedOrder(new Order
        {
            Id = 30,
            OrderRef = "104",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 7, 1, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 301,
            OrderId = 30,
            ItemId = 100,
            QtyOrdered = 756
        });
        if (internalCoversBothItems)
        {
            harness.SeedOrderLine(new OrderLine
            {
                Id = 302,
                OrderId = 30,
                ItemId = 300,
                QtyOrdered = 600
            });
        }

        return harness;
    }
}
