using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletPrintRowsApiIntegrationTests
{
    [Fact]
    public async Task PrintedOneOfOne_AppendNeighbour_InvalidatesSurvivingLabel()
    {
        var harness = BuildStorageConditionsHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        var initial = Assert.Single(await GetProductionRowsAsync(host.Client, 10));

        using var acknowledged = await host.Client.PostAsJsonAsync(
            "/api/orders/10/production-pallets/mark-printed",
            new
            {
                pallets = new[]
                {
                    new { pallet_id = 301L, expected_label_fingerprint = initial.GetProperty("label_fingerprint").GetString() }
                }
            });
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);

        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 302,
            PrdDocId = 20,
            DocLineId = 202,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000101",
            PlannedQty = 100,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 20, 9, 31, 0)
        });

        var rows = await GetProductionRowsAsync(host.Client, 10);
        var surviving = rows.Single(row => row.GetProperty("pallet_id").GetInt64() == 301);
        Assert.Equal(2, surviving.GetProperty("pallet_count").GetInt32());
        Assert.True(surviving.GetProperty("reprint_required").GetBoolean());
        Assert.Equal(ProductionPalletLabelState.ReprintRequired, surviving.GetProperty("label_state").GetString());
    }

    [Fact]
    public async Task PrintedOneOfTwo_RemoveTrailingPlanned_InvalidatesSurvivingLabel()
    {
        var harness = BuildStorageConditionsHarness();
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 302,
            PrdDocId = 20,
            DocLineId = 202,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000101",
            PlannedQty = 100,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 20, 9, 31, 0)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        var initial = (await GetProductionRowsAsync(host.Client, 10))
            .Single(row => row.GetProperty("pallet_id").GetInt64() == 301);

        using var acknowledged = await host.Client.PostAsJsonAsync(
            "/api/orders/10/production-pallets/mark-printed",
            new
            {
                pallets = new[]
                {
                    new { pallet_id = 301L, expected_label_fingerprint = initial.GetProperty("label_fingerprint").GetString() }
                }
            });
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.Equal(1, harness.Store.CancelProductionPallets(new[] { 302L }));

        var surviving = Assert.Single(await GetProductionRowsAsync(host.Client, 10));
        Assert.Equal(1, surviving.GetProperty("pallet_count").GetInt32());
        Assert.True(surviving.GetProperty("reprint_required").GetBoolean());
    }

    [Fact]
    public async Task FingerprintAcknowledgement_WithStaleExpectedFingerprint_FailsClosed()
    {
        var harness = BuildStorageConditionsHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        var initial = Assert.Single(await GetProductionRowsAsync(host.Client, 10));

        var original = harness.Store.GetProductionPalletByHu("HU-0000100")!;
        Assert.True(harness.Store.ResizeSingleItemProductionPallet(original.Id, 350));
        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/production-pallets/mark-printed",
            new
            {
                pallets = new[]
                {
                    new { pallet_id = 301L, expected_label_fingerprint = initial.GetProperty("label_fingerprint").GetString() }
                }
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ProductionPalletLabelContract.Stale, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(ProductionPalletStatus.Planned, harness.Store.GetProductionPalletByHu("HU-0000100")!.Status);
    }

    [Fact]
    public async Task LegacyMarkPrinted_AfterPayloadChanged_DoesNotAcknowledgeCurrentPayload()
    {
        var harness = BuildStorageConditionsHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var printRowsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/orders/10/production-pallets/print-rows");
        printRowsRequest.Headers.Add(ProductionPalletLabelContract.HeaderName, ProductionPalletLabelContract.FingerprintV1);
        using var printRows = await host.Client.SendAsync(printRowsRequest);
        Assert.Equal(HttpStatusCode.OK, printRows.StatusCode);

        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 301,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000100",
            PlannedQty = 350,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 20, 9, 30, 0)
        });

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/production-pallets/mark-printed",
            new { pallet_ids = new[] { 301L } });

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("WPF_PRODUCTION_LABEL_UPGRADE_REQUIRED", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(ProductionPalletStatus.Planned, harness.Store.GetProductionPalletByHu("HU-0000100")!.Status);
    }

    [Fact]
    public async Task LegacyMarkPrinted_ForFreshPlannedPallet_RequiresWpfUpgrade()
    {
        var harness = BuildStorageConditionsHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/production-pallets/mark-printed",
            new { pallet_ids = new[] { 301L } });

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ProductionPalletLabelContract.UpgradeRequired, json.RootElement.GetProperty("error").GetString());
        var pallet = harness.Store.GetProductionPalletByHu("HU-0000100")!;
        Assert.Equal(ProductionPalletStatus.Planned, pallet.Status);
        Assert.Null(pallet.PrintedLabelFingerprint);
    }

    [Fact]
    public async Task LegacyPrintRows_ForReservedHuOnly_RemainsAvailable()
    {
        var harness = BuildReservedHuHarness();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/78/production-pallets/print-rows");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(ProductionPalletPrintSourceType.ReservedHu, row.GetProperty("source_type").GetString());
        Assert.Equal("HU-RESERVED", row.GetProperty("hu_code").GetString());
    }

    [Fact]
    public async Task PrintRows_IncludesStorageConditions_AndDoesNotMutateState()
    {
        var harness = BuildStorageConditionsHarness();

        var ledgerBefore = harness.LedgerEntries.Count;
        var docsBefore = harness.Store.GetDocsByOrder(10).Count;
        var palletsBefore = harness.Store.GetProductionPalletsByDoc(20).Count;

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders/10/production-pallets/print-rows");
        request.Headers.Add(ProductionPalletLabelContract.HeaderName, ProductionPalletLabelContract.FingerprintV1);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("от 0С до +10С", row.GetProperty("storage_conditions").GetString());

        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
        Assert.Equal(docsBefore, harness.Store.GetDocsByOrder(10).Count);
        Assert.Equal(palletsBefore, harness.Store.GetProductionPalletsByDoc(20).Count);
    }

    [Fact]
    public async Task PrintRows_ReservedHuInTwoLocations_Returns400_AndDoesNotMutateState()
    {
        var harness = BuildConflictHarness();

        var ledgerBefore = harness.LedgerEntries.Count;
        var docsBefore = harness.Store.GetDocsByOrder(78).Count;
        var planLinesBefore = harness.Store.GetOrderReceiptPlanLines(78).Count;
        var stockBefore = SnapshotStock(harness);
        var statusBefore = harness.Store.GetOrder(78)!.Status;

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/78/production-pallets/print-rows");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind); // не массив строк печати
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        var message = json.RootElement.GetProperty("message").GetString() ?? string.Empty;
        var error = json.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.False(string.IsNullOrWhiteSpace(error));

        // Сообщение содержит конфликтующий HU и оба места в детерминированном порядке.
        Assert.Contains("HU-CONFLICT", message);
        Assert.Contains("MAIN", message);
        Assert.Contains("DOCK", message);
        Assert.True(message.IndexOf("MAIN", StringComparison.Ordinal)
                    < message.IndexOf("DOCK", StringComparison.Ordinal));

        // Состояние не изменилось.
        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
        Assert.Equal(docsBefore, harness.Store.GetDocsByOrder(78).Count);
        Assert.Equal(planLinesBefore, harness.Store.GetOrderReceiptPlanLines(78).Count);
        Assert.DoesNotContain(harness.Store.GetDocsByOrder(78), doc => harness.Store.HasProductionPallets(doc.Id));
        Assert.Equal(stockBefore, SnapshotStock(harness));
        Assert.Equal(statusBefore, harness.Store.GetOrder(78)!.Status);
    }

    private static (string HuCode, long ItemId, long LocationId, double Qty)[] SnapshotStock(CloseDocumentHarness harness)
    {
        return harness.Store.GetHuStockRows()
            .Select(row => (row.HuCode, row.ItemId, row.LocationId, row.Qty))
            .OrderBy(row => row.HuCode).ThenBy(row => row.ItemId).ThenBy(row => row.LocationId)
            .ToArray();
    }

    private static async Task<JsonElement[]> GetProductionRowsAsync(HttpClient client, long orderId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/production-pallets/print-rows");
        request.Headers.Add(ProductionPalletLabelContract.HeaderName, ProductionPalletLabelContract.FingerprintV1);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("source_type").GetString() == ProductionPalletPrintSourceType.ProductionPallet)
            .Select(row => row.Clone())
            .ToArray();
    }

    private static CloseDocumentHarness BuildConflictHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedLocation(new Location { Id = 2, Code = "DOCK", Name = "Док" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 78,
            OrderRef = "078",
            Type = OrderType.Customer,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 20, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 78,
            ItemId = 100,
            QtyOrdered = 1200
        });
        harness.SeedOrderReceiptPlanLines(78, new OrderReceiptPlanLine
        {
            Id = 501,
            OrderId = 78,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            QtyPlanned = 600,
            ToHu = "HU-CONFLICT",
            SortOrder = 1
        });
        harness.SeedBalance(100, 1, 600, "HU-CONFLICT");
        harness.SeedBalance(100, 2, 400, "HU-CONFLICT");
        return harness;
    }

    private static CloseDocumentHarness BuildReservedHuHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 78,
            OrderRef = "078",
            Type = OrderType.Customer,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 20, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 78,
            ItemId = 100,
            QtyOrdered = 600
        });
        harness.SeedOrderReceiptPlanLines(78, new OrderReceiptPlanLine
        {
            Id = 501,
            OrderId = 78,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            QtyPlanned = 600,
            ToHu = "HU-RESERVED",
            SortOrder = 1
        });
        harness.SeedBalance(100, 1, 600, "HU-RESERVED");
        return harness;
    }

    private static CloseDocumentHarness BuildStorageConditionsHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            StorageConditions = "от 0С до +10С"
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "010",
            Type = OrderType.Internal,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 20, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 600
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000010",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 20, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-0000100"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 301,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000100",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 20, 9, 30, 0)
        });
        return harness;
    }
}
