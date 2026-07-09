using System.Net;
using System.Text;
using System.Text.Json;
using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// Регрессия на INVALID_WAREHOUSE_SELECTION: WPF обязан отправлять вложенные warehouse-строки
/// в snake_case (hu_code/item_id/target_order_line_id). Проверяется и сам serializer WPF, и
/// парсинг сервером через HTTP endpoint.
/// </summary>
public sealed class ApplySelectedCoverageHuBindingSerializationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void BuildApplySelectedCoverageBody_SerializesSnakeCase_NotCamelCase()
    {
        var request = new WpfSelectedCoveragePlanRequest(
            new[] { new WpfSelectedWarehouseHu("HU-WH-001", 100, 101) },
            new long[] { 789 },
            PlanRemainder: true);

        var body = WpfProductionPalletApiService.BuildApplySelectedCoverageBody(request);
        // Сериализуем теми же опциями, что PostAsJsonAsync (JsonSerializerOptions.Web, camelCase-политика).
        var json = JsonSerializer.Serialize(body, WebOptions);

        Assert.Contains("\"selected_warehouse_hus\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hu_code\"", json, StringComparison.Ordinal);
        Assert.Contains("\"item_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"target_order_line_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selected_internal_production_pallet_ids\"", json, StringComparison.Ordinal);
        Assert.Contains("\"plan_remainder\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"apply_selected_coverage_then_plan\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("HuCode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("huCode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("itemId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetOrderLineId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("targetOrderLineId", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanEndpoint_WithSnakeCaseWarehouseSelection_AppliesCoverage_NotInvalidSelection()
    {
        var harness = CreateCustomerHarness(customerQty: 378);
        harness.SeedBalance(100, 1, 378, "HU-WH-001");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        const string payload = """
        {
          "mode": "apply_selected_coverage_then_plan",
          "selected_warehouse_hus": [
            { "hu_code": "HU-WH-001", "item_id": 100, "target_order_line_id": 101 }
          ],
          "selected_internal_production_pallet_ids": [],
          "plan_remainder": true
        }
        """;

        using var response = await host.Client.PostAsync(
            "/api/orders/10/production-pallets/plan",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("INVALID_WAREHOUSE_SELECTION", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("bound_warehouse_hu_count").GetInt32());
        Assert.Contains(
            harness.Store.GetOrderReceiptPlanLines(10),
            line => line.ToHu == "HU-WH-001");
    }

    [Fact]
    public async Task PlanEndpoint_WithCamelCaseWarehouseSelection_FailsInvalidSelection_DocumentsContract()
    {
        // camelCase — это ровно тот баг, который приводил к INVALID_WAREHOUSE_SELECTION.
        var harness = CreateCustomerHarness(customerQty: 378);
        harness.SeedBalance(100, 1, 378, "HU-WH-001");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        const string payload = """
        {
          "mode": "apply_selected_coverage_then_plan",
          "selected_warehouse_hus": [
            { "huCode": "HU-WH-001", "itemId": 100, "targetOrderLineId": 101 }
          ],
          "plan_remainder": true
        }
        """;

        using var response = await host.Client.PostAsync(
            "/api/orders/10/production-pallets/plan",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("INVALID_WAREHOUSE_SELECTION", body, StringComparison.Ordinal);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));
    }

    [Fact]
    public async Task PlanEndpoint_WithBrokenWarehouseRow_Returns409_WithoutPartialWrites()
    {
        var harness = CreateCustomerHarness(customerQty: 378);
        harness.SeedBalance(100, 1, 378, "HU-WH-001");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        const string payload = """
        {
          "mode": "apply_selected_coverage_then_plan",
          "selected_warehouse_hus": [
            { "hu_code": "", "item_id": 0, "target_order_line_id": 0 }
          ],
          "plan_remainder": true
        }
        """;

        using var response = await host.Client.PostAsync(
            "/api/orders/10/production-pallets/plan",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("INVALID_WAREHOUSE_SELECTION", body, StringComparison.Ordinal);
        Assert.Empty(harness.Store.GetOrderReceiptPlanLines(10));
        Assert.Empty(harness.Store.GetDocsByOrder(10)
            .SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id)));
    }

    private static CloseDocumentHarness CreateCustomerHarness(double customerQty)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Аджика",
            BaseUom = "шт",
            MaxQtyPerHu = 378
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
            QtyOrdered = customerQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return harness;
    }
}
