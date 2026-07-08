using System.Net;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletInternalSupplyWarningTests
{
    [Theory]
    [InlineData(OrderStatus.InProgress, "IN_PROGRESS")]
    [InlineData(OrderStatus.Draft, "DRAFT")]
    public void Warning_WhenCustomerLineMatchesOpenInternalOrder(OrderStatus internalStatus, string expectedStatus)
    {
        var harness = CreateCustomerWithInternalHarness(internalStatus: internalStatus);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        Assert.True(warning.HasWarning);
        Assert.Equal(10, warning.OrderId);
        Assert.Equal("086", warning.OrderRef);
        var line = Assert.Single(warning.Lines);
        Assert.Equal(101, line.OrderLineId);
        Assert.Equal(100, line.ItemId);
        Assert.Equal("Аджика", line.ItemName);
        Assert.Equal(378, line.WouldPlanQty);
        Assert.Equal(30, line.InternalOrderId);
        Assert.Equal("104", line.InternalOrderRef);
        Assert.Equal(expectedStatus, line.InternalOrderStatus);
        Assert.Equal(756, line.ExpectedQty);
        Assert.Contains("уже ожидается выпуск во внутреннем заказе", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Аджика", warning.Message, StringComparison.Ordinal);
        Assert.Contains("104", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Merged)]
    public void NoWarning_WhenInternalOrderIsTerminal(OrderStatus internalStatus)
    {
        var harness = CreateCustomerWithInternalHarness(internalStatus: internalStatus);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void NoWarning_WhenInternalOrderHasDifferentItem()
    {
        var harness = CreateCustomerWithInternalHarness(internalItemId: 200);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void ExpectedQty_CountsOnlyRemainingAfterProducedReceipt()
    {
        var harness = CreateCustomerWithInternalHarness();
        SeedClosedInternalReceipt(harness, producedQty: 300);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        var line = Assert.Single(warning.Lines);
        Assert.Equal(456, line.ExpectedQty);
    }

    [Fact]
    public void NoWarning_WhenInternalOrderFullyProduced()
    {
        var harness = CreateCustomerWithInternalHarness();
        SeedClosedInternalReceipt(harness, producedQty: 756);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void NoWarning_WhenCustomerLineFullyCoveredByBoundHu()
    {
        var harness = CreateCustomerWithInternalHarness();
        harness.SeedBalance(100, 1, 378, "HU-900101");
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            QtyPlanned = 378,
            ToLocationId = 1,
            ToHu = "HU-900101",
            SortOrder = 1
        });
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void NoWarning_ForInternalOrderItself()
    {
        var harness = CreateCustomerWithInternalHarness();
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(30);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void Preview_UnknownOrder_Throws()
    {
        var harness = CreateCustomerWithInternalHarness();
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetCustomerPlanInternalSupplyWarning(999));
        Assert.Equal("Заказ не найден.", ex.Message);
    }

    [Fact]
    public void Preview_DoesNotMutateState()
    {
        var harness = CreateCustomerWithInternalHarness();
        var ledgerBefore = harness.LedgerEntries.Count;
        var customerDocsBefore = harness.Store.GetDocsByOrder(10).Count;
        var internalDocsBefore = harness.Store.GetDocsByOrder(30).Count;
        var planLinesBefore = harness.Store.GetOrderReceiptPlanLines(10).Count;
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPlanInternalSupplyWarning(10);

        Assert.True(warning.HasWarning);
        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
        Assert.Equal(customerDocsBefore, harness.Store.GetDocsByOrder(10).Count);
        Assert.Equal(internalDocsBefore, harness.Store.GetDocsByOrder(30).Count);
        Assert.Equal(planLinesBefore, harness.Store.GetOrderReceiptPlanLines(10).Count);
        Assert.Equal(OrderStatus.InProgress, harness.Store.GetOrder(10)!.Status);
        Assert.Equal(OrderStatus.InProgress, harness.Store.GetOrder(30)!.Status);
    }

    [Fact]
    public void PlanAfterPreview_UsesSameServerDerivedFormula()
    {
        var previewedHarness = CreateCustomerWithInternalHarness();
        var previewedService = new ProductionPalletService(previewedHarness.Store);
        previewedService.GetCustomerPlanInternalSupplyWarning(10);
        var previewedPlan = previewedService.PlanOrder(10);

        var directHarness = CreateCustomerWithInternalHarness();
        var directPlan = new ProductionPalletService(directHarness.Store).PlanOrder(10);

        Assert.Equal(directPlan.Summary.PlannedQty, previewedPlan.Summary.PlannedQty);
        Assert.Equal(directPlan.Summary.PlannedPalletCount, previewedPlan.Summary.PlannedPalletCount);
        Assert.Equal(378, previewedPlan.Summary.PlannedQty);
        Assert.Equal(1, previewedPlan.Summary.PlannedPalletCount);
    }

    [Fact]
    public async Task Api_ReturnsSnakeCaseContract_AndDoesNotMutateState()
    {
        var harness = CreateCustomerWithInternalHarness();
        var ledgerBefore = harness.LedgerEntries.Count;
        var docsBefore = harness.Store.GetDocsByOrder(10).Count;

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/10/production-pallets/internal-supply-warning");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(10, json.RootElement.GetProperty("order_id").GetInt64());
        Assert.Equal("086", json.RootElement.GetProperty("order_ref").GetString());
        Assert.True(json.RootElement.GetProperty("has_warning").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
        var line = Assert.Single(json.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(101, line.GetProperty("customer_order_line_id").GetInt64());
        Assert.Equal(100, line.GetProperty("item_id").GetInt64());
        Assert.Equal("Аджика", line.GetProperty("item_name").GetString());
        Assert.Equal(378, line.GetProperty("would_plan_qty").GetDouble());
        Assert.Equal(30, line.GetProperty("internal_order_id").GetInt64());
        Assert.Equal("104", line.GetProperty("internal_order_ref").GetString());
        Assert.Equal("IN_PROGRESS", line.GetProperty("internal_status").GetString());
        Assert.Equal(756, line.GetProperty("expected_qty").GetDouble());

        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
        Assert.Equal(docsBefore, harness.Store.GetDocsByOrder(10).Count);
    }

    [Fact]
    public async Task Api_UnknownOrder_Returns400()
    {
        var harness = CreateCustomerWithInternalHarness();

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/999/production-pallets/internal-supply-warning");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public void WpfApi_ParsesPreviewContract_AndSendsNoQuantities()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfProductionPalletApiService.cs");
        var method = SliceMethod(
            source,
            "    public async Task<WpfProductionPalletInternalSupplyWarningApiResult> TryGetInternalSupplyWarningAsync(",
            "    public async Task<WpfProductionPalletPrintRowsApiResult> TryGetPrintRowsAsync(");

        Assert.Contains("/api/orders/{orderId}/production-pallets/internal-supply-warning", method, StringComparison.Ordinal);
        Assert.Contains("client.GetAsync", method, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode.NotFound", method, StringComparison.Ordinal);
        Assert.Contains("EndpointMissing", method, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsJsonAsync", method, StringComparison.Ordinal);

        Assert.Contains("[JsonPropertyName(\"has_warning\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"would_plan_qty\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"internal_order_ref\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"expected_qty\")]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfPlanClick_ConfirmsInternalSupplyBeforePlan_WithoutClientQuantities()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var clickMethod = SliceMethod(
            source,
            "    private async void PlanPallets_Click(",
            "    private async Task<bool> ConfirmInternalSupplyBeforePlanAsync(");
        var confirmMethod = SliceMethod(
            source,
            "    private async Task<bool> ConfirmInternalSupplyBeforePlanAsync(",
            "    private void ReadyHuBinding_Click(");

        // Подтверждение выполняется до вызова POST /plan.
        var confirmIndex = clickMethod.IndexOf("ConfirmInternalSupplyBeforePlanAsync", StringComparison.Ordinal);
        var planIndex = clickMethod.IndexOf("TryPlanOrderAsync", StringComparison.Ordinal);
        Assert.True(confirmIndex >= 0 && planIndex > confirmIndex);

        // Отмена в диалоге возвращает false и не вызывает POST /plan; qty на сервер не передаются.
        Assert.Contains("TryGetInternalSupplyWarningAsync", confirmMethod, StringComparison.Ordinal);
        Assert.Contains("IsEndpointMissing", confirmMethod, StringComparison.Ordinal);
        Assert.Contains("Не удалось проверить ожидаемый INTERNAL-выпуск.", confirmMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("qty", confirmMethod, StringComparison.OrdinalIgnoreCase);
    }

    private static CloseDocumentHarness CreateCustomerWithInternalHarness(
        OrderStatus internalStatus = OrderStatus.InProgress,
        long internalItemId = 100)
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
        if (internalItemId != 100)
        {
            harness.SeedItem(new Item
            {
                Id = internalItemId,
                Name = "Другой товар",
                BaseUom = "шт",
                MaxQtyPerHu = 600
            });
        }

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
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        harness.SeedOrder(new Order
        {
            Id = 30,
            OrderRef = "104",
            Type = OrderType.Internal,
            Status = internalStatus,
            CreatedAt = new DateTime(2026, 7, 1, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 301,
            OrderId = 30,
            ItemId = internalItemId,
            QtyOrdered = 756
        });

        return harness;
    }

    private static void SeedClosedInternalReceipt(CloseDocumentHarness harness, double producedQty)
    {
        harness.SeedDoc(new Doc
        {
            Id = 40,
            DocRef = "PRD-2026-000040",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 30,
            OrderRef = "104",
            CreatedAt = new DateTime(2026, 7, 2, 8, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 401,
            DocId = 40,
            OrderLineId = 301,
            ItemId = 100,
            Qty = producedQty,
            ToLocationId = 1,
            ToHu = "HU-0000401"
        });
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");

        return source[start..end];
    }
}
