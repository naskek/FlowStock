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

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

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

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void NoWarning_WhenInternalOrderHasDifferentItem()
    {
        var harness = CreateCustomerWithInternalHarness(internalItemId: 200);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void ExpectedQty_CountsOnlyRemainingAfterProducedReceipt()
    {
        var harness = CreateCustomerWithInternalHarness();
        SeedClosedInternalReceipt(harness, producedQty: 300);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

        var line = Assert.Single(warning.Lines);
        Assert.Equal(456, line.ExpectedQty);
    }

    [Fact]
    public void NoWarning_WhenInternalOrderFullyProduced()
    {
        var harness = CreateCustomerWithInternalHarness();
        SeedClosedInternalReceipt(harness, producedQty: 756);
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

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

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void NoWarning_ForInternalOrderItself()
    {
        var harness = CreateCustomerWithInternalHarness();
        var service = new ProductionPalletService(harness.Store);

        var warning = service.GetCustomerPrePlanCoveragePreview(30);

        Assert.False(warning.HasWarning);
        Assert.Empty(warning.Lines);
    }

    [Fact]
    public void Preview_UnknownOrder_Throws()
    {
        var harness = CreateCustomerWithInternalHarness();
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetCustomerPrePlanCoveragePreview(999));
        Assert.Equal("Заказ не найден.", ex.Message);
    }

    [Fact]
    public void Preview_ReportsWouldPlanSafeAndWarningLineCounts()
    {
        var harness = CreateCustomerWithInternalHarness();
        harness.SeedItem(new Item
        {
            Id = 300,
            Name = "Хрен",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 300,
            QtyOrdered = 500,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        var service = new ProductionPalletService(harness.Store);

        var preview = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.True(preview.HasWarning);
        Assert.Equal(2, preview.WouldPlanLineCount);
        Assert.Equal(1, preview.SafeLineCount);
        Assert.Equal(1, preview.WarningLineCount);
    }

    [Fact]
    public void Preview_ReportsFreeWarehouseHuForWouldPlanLines()
    {
        var harness = CreateCustomerWithInternalHarness(internalItemId: 200);
        harness.SeedBalance(100, 1, 200, "HU-FREE-01");
        harness.SeedBalance(100, 1, 178, "HU-FREE-02");
        var service = new ProductionPalletService(harness.Store);

        var preview = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.False(preview.HasWarning);
        Assert.True(preview.HasFreeWarehouseHu);
        var freeLine = Assert.Single(preview.FreeWarehouseHuLines);
        Assert.Equal(101, freeLine.OrderLineId);
        Assert.Equal(100, freeLine.ItemId);
        Assert.Equal(2, freeLine.FreeHuCount);
        Assert.Equal(378, freeLine.FreeHuQty);
        Assert.Contains("свободные складские HU", preview.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_HuReservedByOtherOrder_IsNotFree()
    {
        var harness = CreateCustomerWithInternalHarness(internalItemId: 200);
        harness.SeedBalance(100, 1, 378, "HU-OTHER-01");
        harness.SeedOrder(new Order
        {
            Id = 50,
            OrderRef = "050",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerName = "Другой клиент",
            CreatedAt = new DateTime(2026, 7, 1, 10, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 501,
            OrderId = 50,
            ItemId = 100,
            QtyOrdered = 378,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(50, new OrderReceiptPlanLine
        {
            Id = 5001,
            OrderId = 50,
            OrderLineId = 501,
            ItemId = 100,
            QtyPlanned = 378,
            ToLocationId = 1,
            ToHu = "HU-OTHER-01",
            SortOrder = 1
        });
        var service = new ProductionPalletService(harness.Store);

        var preview = service.GetCustomerPrePlanCoveragePreview(10);

        Assert.False(preview.HasFreeWarehouseHu);
        Assert.Empty(preview.FreeWarehouseHuLines);
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

        var warning = service.GetCustomerPrePlanCoveragePreview(10);

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
        previewedService.GetCustomerPrePlanCoveragePreview(10);
        var previewedPlan = previewedService.PlanOrder(10);

        var directHarness = CreateCustomerWithInternalHarness();
        var directPlan = new ProductionPalletService(directHarness.Store).PlanOrder(10);

        Assert.Equal(directPlan.Summary.PlannedQty, previewedPlan.Summary.PlannedQty);
        Assert.Equal(directPlan.Summary.PlannedPalletCount, previewedPlan.Summary.PlannedPalletCount);
        Assert.Equal(378, previewedPlan.Summary.PlannedQty);
        Assert.Equal(1, previewedPlan.Summary.PlannedPalletCount);
    }

    [Theory]
    [InlineData("/api/orders/10/production-pallets/pre-plan-coverage-preview")]
    [InlineData("/api/orders/10/production-pallets/internal-supply-warning")] // compatibility alias
    public async Task Api_ReturnsSnakeCaseContract_AndDoesNotMutateState(string route)
    {
        var harness = CreateCustomerWithInternalHarness();
        var ledgerBefore = harness.LedgerEntries.Count;
        var docsBefore = harness.Store.GetDocsByOrder(10).Count;

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync(route);
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

        Assert.Equal(1, json.RootElement.GetProperty("would_plan_line_count").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("safe_line_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("warning_line_count").GetInt32());
        Assert.False(json.RootElement.GetProperty("has_free_warehouse_hu").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("free_warehouse_hu").EnumerateArray());

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
            "    public async Task<WpfPrePlanCoveragePreviewApiResult> TryGetPrePlanCoveragePreviewAsync(",
            "    public async Task<WpfProductionPalletPrintRowsApiResult> TryGetPrintRowsAsync(");

        Assert.Contains("/api/orders/{orderId}/production-pallets/pre-plan-coverage-preview", method, StringComparison.Ordinal);
        Assert.Contains("client.GetAsync", method, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode.NotFound", method, StringComparison.Ordinal);
        Assert.Contains("EndpointMissing", method, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsJsonAsync", method, StringComparison.Ordinal);

        Assert.Contains("[JsonPropertyName(\"has_warning\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"would_plan_qty\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"internal_order_ref\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"expected_qty\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"would_plan_line_count\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"safe_line_count\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"has_free_warehouse_hu\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"warehouse_hu_candidates\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"internal_planned_hu_candidates\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"adoptable_internal_planned_hus\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"adoption_skipped_candidates\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"projected_adopted_pallet_count\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"will_require_reprint\")]", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"skipped_lines\")]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfPlanClick_RunsPrePlanCoverageFlowBeforePlan_WithoutClientQuantities()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var clickMethod = SliceMethod(
            source,
            "    private async void PlanPallets_Click(",
            "    private enum PrePlanFlowDecision");
        var flowMethod = SliceMethod(
            source,
            "    private async Task<PrePlanFlowDecision> RunPrePlanCoverageFlowAsync(",
            "    private static string BuildProjectedAdoptionSummary(");

        // Pre-plan flow выполняется до вызова POST /plan.
        var flowIndex = clickMethod.IndexOf("RunPrePlanCoverageFlowAsync", StringComparison.Ordinal);
        var planIndex = clickMethod.IndexOf("TryPlanOrderAsync", StringComparison.Ordinal);
        Assert.True(flowIndex >= 0 && planIndex > flowIndex);

        // Safe-only передаёт только режим, qty/строки не передаются.
        Assert.Contains("WpfProductionPalletPlanMode.SkipInternalSupply", clickMethod, StringComparison.Ordinal);
        Assert.Contains("WpfProductionPalletPlanMode.AdoptInternalThenPlan", clickMethod, StringComparison.Ordinal);
        Assert.Contains("WpfProductionPalletPlanMode.ApplySelectedCoverageThenPlan", clickMethod, StringComparison.Ordinal);
        Assert.Contains("adopt_internal_then_plan", clickMethod, StringComparison.Ordinal);
        Assert.Contains("apply_selected_coverage_then_plan", clickMethod, StringComparison.Ordinal);

        // Unified preview: WPF показывает кандидаты и отправляет только selected HU ids/codes как preference.
        Assert.Contains("TryGetPrePlanCoveragePreviewAsync", flowMethod, StringComparison.Ordinal);
        Assert.Contains("IsEndpointMissing", flowMethod, StringComparison.Ordinal);
        Assert.Contains("Не удалось проверить складские HU и ожидаемый INTERNAL-выпуск.", flowMethod, StringComparison.Ordinal);
        Assert.Contains("WarehouseHuCandidatesOrEmpty", flowMethod, StringComparison.Ordinal);
        Assert.Contains("InternalPlannedHuCandidatesOrEmpty", flowMethod, StringComparison.Ordinal);
        Assert.Contains("BuildSelectedCoverageRequest", flowMethod, StringComparison.Ordinal);
        Assert.Contains("PrePlanDialogAction.ApplySelectedCoverageThenPlan", flowMethod, StringComparison.Ordinal);
        Assert.Contains("BuildProjectedAdoptionSummary", flowMethod, StringComparison.Ordinal);
        // Unified flow — единственный путь покрытия для CUSTOMER: legacy ready-HU окно не открывается.
        Assert.DoesNotContain("ReadyHuBindingWindow", clickMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadyHuBindingWindow", flowMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPlanOrderAsync", flowMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("qty", flowMethod, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WpfPrePlanCoverageDialog_DisabledCandidatesAreVisibleButNotSent()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "PrePlanCoverageDialog.xaml");
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "PrePlanCoverageDialog.xaml.cs");
        var buildRequestMethod = SliceMethod(
            source,
            "    public WpfSelectedCoveragePlanRequest BuildSelectedCoverageRequest()",
            "    private void PlanAll_Click(");

        Assert.Contains("IsEnabled=\"{Binding CanSelect}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding DisabledReason}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("public bool CanSelect", source, StringComparison.Ordinal);
        Assert.Contains("public string DisabledReason", source, StringComparison.Ordinal);
        Assert.Contains("IsSelected = candidate.SelectedByDefault && CanSelect", source, StringComparison.Ordinal);
        Assert.Contains("row.IsSelected && row.CanSelect", buildRequestMethod, StringComparison.Ordinal);
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
