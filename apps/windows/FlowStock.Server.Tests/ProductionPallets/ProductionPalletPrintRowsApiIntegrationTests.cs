using System.Net;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletPrintRowsApiIntegrationTests
{
    [Fact]
    public async Task PrintRows_IncludesStorageConditions_AndDoesNotMutateState()
    {
        var harness = BuildStorageConditionsHarness();

        var ledgerBefore = harness.LedgerEntries.Count;
        var docsBefore = harness.Store.GetDocsByOrder(10).Count;
        var palletsBefore = harness.Store.GetProductionPalletsByDoc(20).Count;

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/orders/10/production-pallets/print-rows");
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
