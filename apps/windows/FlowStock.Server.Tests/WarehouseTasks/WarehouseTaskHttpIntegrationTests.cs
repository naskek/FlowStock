using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Services.Warehouse;
using FlowStock.Server.Tests.WarehouseTasks.Infrastructure;
using Xunit;

namespace FlowStock.Server.Tests.WarehouseTasks;

public sealed class WarehouseTaskHttpIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task HttpTsdScan_DoesNotPostLedger_UntilConfirmExecution()
    {
        var harness = new WarehouseBundleServiceHarness();
        const string huCode = "HU-HTTP-001";
        var moveQty = 4d;
        var (itemId, fromLoc, toLoc) = harness.SeedMoveScenario(huCode, qty: moveQty);
        await using var host = await WarehouseTaskHttpHost.StartAsync(harness);

        var bundleId = await CreateApproveMoveBundleAsync(host, huCode, itemId, fromLoc, toLoc, moveQty);
        var taskId = await GetSingleTaskIdAsync(host, bundleId);

        Assert.Empty(harness.LedgerEntries);

        await PostOkAsync(host.Client, $"/api/tsd/tasks/{taskId}/start", new { device_id = "TSD-HTTP" });
        Assert.Empty(harness.LedgerEntries);

        await PostOkAsync(
            host.Client,
            $"/api/tsd/tasks/{taskId}/scan",
            new { device_id = "TSD-HTTP", barcode = huCode, scan_type = "HU" });
        Assert.Empty(harness.LedgerEntries);

        await PostOkAsync(
            host.Client,
            $"/api/tsd/tasks/{taskId}/scan",
            new { device_id = "TSD-HTTP", barcode = "SHIP-01", scan_type = "LOCATION" });
        Assert.Empty(harness.LedgerEntries);

        await PostOkAsync(host.Client, $"/api/tsd/tasks/{taskId}/complete", new { device_id = "TSD-HTTP" });
        Assert.Empty(harness.LedgerEntries);

        var bundleAfterTsd = harness.Store.GetWarehouseActionBundle(bundleId);
        Assert.Equal(WarehouseBundleStatus.Executed, bundleAfterTsd!.Status);

        await PostOkAsync(host.Client, $"/api/planner/bundles/{bundleId}/confirm-execution", new { actor = "WPF" });
        Assert.Equal(2, harness.LedgerEntries.Count);

        await PostOkAsync(host.Client, $"/api/planner/bundles/{bundleId}/confirm-execution", new { actor = "WPF" });
        Assert.Equal(2, harness.LedgerEntries.Count);
    }

    [Fact]
    public async Task HttpTsdComplete_WithoutConfirm_LeavesBundleExecutedAndLedgerEmpty()
    {
        var harness = new WarehouseBundleServiceHarness();
        const string huCode = "HU-HTTP-002";
        var moveQty = 2d;
        var (itemId, fromLoc, toLoc) = harness.SeedMoveScenario(huCode, qty: moveQty);
        await using var host = await WarehouseTaskHttpHost.StartAsync(harness);

        var bundleId = await CreateApproveMoveBundleAsync(host, huCode, itemId, fromLoc, toLoc, moveQty);
        var taskId = await GetSingleTaskIdAsync(host, bundleId);

        await PostOkAsync(host.Client, $"/api/tsd/tasks/{taskId}/start", new { device_id = "TSD-HTTP" });
        await PostOkAsync(
            host.Client,
            $"/api/tsd/tasks/{taskId}/scan",
            new { device_id = "TSD-HTTP", barcode = huCode, scan_type = "HU" });
        await PostOkAsync(
            host.Client,
            $"/api/tsd/tasks/{taskId}/scan",
            new { device_id = "TSD-HTTP", barcode = "SHIP-01", scan_type = "LOCATION" });
        await PostOkAsync(host.Client, $"/api/tsd/tasks/{taskId}/complete", new { device_id = "TSD-HTTP" });

        var bundle = harness.Store.GetWarehouseActionBundle(bundleId);
        Assert.Equal(WarehouseBundleStatus.Executed, bundle!.Status);
        Assert.Empty(harness.LedgerEntries);
    }

    private static async Task<long> CreateApproveMoveBundleAsync(
        WarehouseTaskHttpHost host,
        string huCode,
        long itemId,
        long fromLoc,
        long toLoc,
        double qty)
    {
        using var createResponse = await host.Client.PostAsJsonAsync(
            "/api/planner/bundles",
            new { source = WarehouseBundleSource.Wpf, created_by = "http-test" },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createPayload = await createResponse.Content.ReadFromJsonAsync<OperationResponse>(JsonOptions);
        Assert.NotNull(createPayload);
        Assert.True(createPayload.Success);
        var bundleId = createPayload.BundleId!.Value;

        var payloadJson = WarehousePayloadParser.ToJson(new WarehouseMoveHuPayload
        {
            HuCode = huCode,
            ItemId = itemId,
            Qty = qty,
            FromLocationId = fromLoc,
            ToLocationId = toLoc
        });

        using var lineResponse = await host.Client.PostAsJsonAsync(
            $"/api/planner/bundles/{bundleId}/lines",
            new
            {
                action_type = WarehouseActionType.MoveHu,
                hu_code = huCode,
                item_id = itemId,
                qty,
                from_location_id = fromLoc,
                to_location_id = toLoc,
                payload_json = payloadJson
            },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, lineResponse.StatusCode);

        await PostOkAsync(host.Client, $"/api/planner/bundles/{bundleId}/submit", new { actor = "http-test" });
        await PostOkAsync(host.Client, $"/api/planner/bundles/{bundleId}/approve", new { approved_by = "supervisor" });

        return bundleId;
    }

    private static async Task<long> GetSingleTaskIdAsync(WarehouseTaskHttpHost host, long bundleId)
    {
        using var response = await host.Client.GetAsync($"/api/planner/bundles/{bundleId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var tasks = json.RootElement.GetProperty("tasks");
        Assert.Equal(1, tasks.GetArrayLength());
        return tasks[0].GetProperty("task").GetProperty("id").GetInt64();
    }

    private static async Task PostOkAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, JsonOptions);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"POST {url} returned {response.StatusCode}: {raw}");
        var payload = JsonSerializer.Deserialize<OperationResponse>(raw, JsonOptions);
        Assert.NotNull(payload);
        Assert.True(payload.Success, raw);
    }

    private sealed class OperationResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("bundle_id")]
        public long? BundleId { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }
}
