using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// Wire-contract tests for the pallet constructor endpoints. The WPF client binds to these
/// exact snake_case field names; they must not depend on accidental CLR property serialization.
/// </summary>
public sealed class ProductionPalletExplicitPlanApiContractTests
{
    [Fact]
    public async Task PlanPreview_ReturnsThreeSectionsAndFingerprint_InSnakeCase()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);

        using var response = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(10, root.GetProperty("order_id").GetInt64());
        Assert.True(root.GetProperty("production_required").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("preview_fingerprint").GetString()));
        Assert.Equal(3, root.GetProperty("suggested_pallets").GetArrayLength());
        Assert.Equal(0, root.GetProperty("open_plan_pallets").GetArrayLength());
        Assert.Equal(0, root.GetProperty("historical_pallets").GetArrayLength());

        var firstSuggested = root.GetProperty("suggested_pallets")[0];
        Assert.Equal(2250, firstSuggested.GetProperty("capacity_qty").GetDouble(), 3);
        Assert.False(firstSuggested.GetProperty("is_mixed").GetBoolean());
        var component = firstSuggested.GetProperty("components")[0];
        Assert.Equal(101, component.GetProperty("order_line_id").GetInt64());
        Assert.Equal(2250, component.GetProperty("qty").GetDouble(), 3);

        var line = root.GetProperty("lines")[0];
        Assert.Equal(101, line.GetProperty("order_line_id").GetInt64());
        Assert.Equal(3375, line.GetProperty("shortfall_qty").GetDouble(), 3);
        Assert.Equal(2250, line.GetProperty("max_qty_per_hu").GetDouble(), 3);
    }

    [Fact]
    public async Task PlanExplicit_PartialAllocation_ReturnsStructuredDetailsLines_InSnakeCase()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        previewResponse.EnsureSuccessStatusCode();
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var fingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();

        using var response = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", new
        {
            preview_fingerprint = fingerprint,
            pallets = new[]
            {
                new { components = new[] { new { order_line_id = 101, qty = 2250d } } }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("LINE_ALLOCATION_MISMATCH", root.GetProperty("error_code").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());

        var lines = root.GetProperty("details").GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        var line101 = FindLine(lines, 101);
        Assert.Equal(3375, line101.GetProperty("required_qty").GetDouble(), 3);
        Assert.Equal(2250, line101.GetProperty("allocated_qty").GetDouble(), 3);
        Assert.Equal(-1125, line101.GetProperty("difference_qty").GetDouble(), 3);
        var line102 = FindLine(lines, 102);
        Assert.Equal(1125, line102.GetProperty("required_qty").GetDouble(), 3);
        Assert.Equal(0, line102.GetProperty("allocated_qty").GetDouble(), 3);
    }

    [Fact]
    public async Task PlanExplicit_StaleFingerprint_Returns409WithCurrentFingerprint()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);

        using var response = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", new
        {
            preview_fingerprint = "STALE",
            pallets = new[]
            {
                new { components = new[] { new { order_line_id = 101, qty = 2250d } } }
            }
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("PLAN_PREVIEW_STALE", root.GetProperty("error_code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("current_preview_fingerprint").GetString()));
    }

    [Fact]
    public async Task PlanExplicit_ValidMixedDelta_ReturnsPlanAndNewFingerprint()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var fingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();

        using var response = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", new
        {
            preview_fingerprint = fingerprint,
            pallets = new object[]
            {
                new { components = new[] { new { order_line_id = 101, qty = 2250d } } },
                new
                {
                    components = new[]
                    {
                        new { order_line_id = 101, qty = 1125d },
                        new { order_line_id = 102, qty = 1125d }
                    }
                }
            }
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.NotEqual(fingerprint, root.GetProperty("preview_fingerprint").GetString());
        Assert.Equal(2, root.GetProperty("plan").GetProperty("planned_pallet_count").GetInt32());
    }

    [Fact]
    public async Task PlanExplicit_ExactAllocationPlusEmptyPallet_Returns400InvalidPalletPlan_WithoutPartialWrites()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var fingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();

        using var response = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", new
        {
            preview_fingerprint = fingerprint,
            pallets = new object[]
            {
                new { components = new[] { new { order_line_id = 101, qty = 2250d } } },
                new
                {
                    components = new[]
                    {
                        new { order_line_id = 101, qty = 1125d },
                        new { order_line_id = 102, qty = 1125d }
                    }
                },
                new { components = Array.Empty<object>() }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_PALLET_PLAN", document.RootElement.GetProperty("error_code").GetString());
        await AssertNoPlanWritesAsync(host);
    }

    [Fact]
    public async Task PlanExplicit_ZeroComponentQty_Returns400InvalidPalletPlan_WithoutPartialWrites()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var fingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();

        using var response = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", new
        {
            preview_fingerprint = fingerprint,
            pallets = new object[]
            {
                new { components = new[] { new { order_line_id = 101, qty = 2250d } } },
                new
                {
                    components = new object[]
                    {
                        new { order_line_id = 101, qty = 1125d },
                        new { order_line_id = 102, qty = 1125d },
                        new { order_line_id = 102, qty = -5d }
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_PALLET_PLAN", document.RootElement.GetProperty("error_code").GetString());
        await AssertNoPlanWritesAsync(host);
    }

    [Fact]
    public async Task PlanExplicit_SuccessFingerprintIsAtomic_AndResubmitWithOldFingerprintIsStale()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var oldFingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();
        var body = new
        {
            preview_fingerprint = oldFingerprint,
            pallets = new object[]
            {
                new { components = new[] { new { order_line_id = 101, qty = 2250d } } },
                new
                {
                    components = new[]
                    {
                        new { order_line_id = 101, qty = 1125d },
                        new { order_line_id = 102, qty = 1125d }
                    }
                }
            }
        };

        using var confirmResponse = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", body);
        confirmResponse.EnsureSuccessStatusCode();
        using var confirmDocument = JsonDocument.Parse(await confirmResponse.Content.ReadAsStringAsync());
        var atomicFingerprint = confirmDocument.RootElement.GetProperty("preview_fingerprint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(atomicFingerprint));
        Assert.NotEqual(oldFingerprint, atomicFingerprint);

        // The success response fingerprint equals the current server preview state.
        using var afterPreviewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var afterPreviewDocument = JsonDocument.Parse(await afterPreviewResponse.Content.ReadAsStringAsync());
        Assert.Equal(atomicFingerprint, afterPreviewDocument.RootElement.GetProperty("preview_fingerprint").GetString());

        using var repeatResponse = await host.Client.PostAsJsonAsync("/api/orders/10/production-pallets/plan-explicit", body);
        Assert.Equal(HttpStatusCode.Conflict, repeatResponse.StatusCode);
        using var repeatDocument = JsonDocument.Parse(await repeatResponse.Content.ReadAsStringAsync());
        Assert.Equal("PLAN_PREVIEW_STALE", repeatDocument.RootElement.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task PlanExplicit_NullPalletElement_Returns400InvalidPalletPlan_WithoutPartialWrites()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var fingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();

        var json = $$"""{ "preview_fingerprint": "{{fingerprint}}", "pallets": [ null ] }""";
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/api/orders/10/production-pallets/plan-explicit", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_PALLET_PLAN", document.RootElement.GetProperty("error_code").GetString());
        await AssertNoPlanWritesAsync(host);
    }

    [Fact]
    public async Task PlanExplicit_NullComponentElement_Returns400InvalidPalletPlan_WithoutPartialWrites()
    {
        var harness = CreateConstructorExampleHarness();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);
        using var previewResponse = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var previewDocument = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var fingerprint = previewDocument.RootElement.GetProperty("preview_fingerprint").GetString();

        var json = $$"""{ "preview_fingerprint": "{{fingerprint}}", "pallets": [ { "components": [ null ] } ] }""";
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/api/orders/10/production-pallets/plan-explicit", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_PALLET_PLAN", document.RootElement.GetProperty("error_code").GetString());
        await AssertNoPlanWritesAsync(host);
    }

    private static async Task AssertNoPlanWritesAsync(ProductionPalletTsdHttpHost host)
    {
        using var response = await host.Client.GetAsync("/api/orders/10/production-pallets/plan-preview");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("production_required").GetBoolean());
        Assert.Equal(0, root.GetProperty("open_plan_pallets").GetArrayLength());
        Assert.Equal(0, root.GetProperty("historical_pallets").GetArrayLength());
    }

    private static JsonElement FindLine(JsonElement lines, long orderLineId)
    {
        foreach (var line in lines.EnumerateArray())
        {
            if (line.GetProperty("order_line_id").GetInt64() == orderLineId)
            {
                return line;
            }
        }

        throw new Xunit.Sdk.XunitException($"details.lines missing order_line_id={orderLineId}");
    }

    private static CloseDocumentHarness CreateConstructorExampleHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Хрен столовый", BaseUom = "шт", MaxQtyPerHu = 2250 });
        harness.SeedItem(new Item { Id = 200, Name = "Хрен со свёклой", BaseUom = "шт", MaxQtyPerHu = 2250 });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 100, QtyOrdered = 3375 });
        harness.SeedOrderLine(new OrderLine { Id = 102, OrderId = 10, ItemId = 200, QtyOrdered = 1125 });
        return harness;
    }
}
