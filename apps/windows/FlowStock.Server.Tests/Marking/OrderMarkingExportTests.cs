using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Marking;

public sealed class OrderMarkingExportTests
{
    [Fact]
    public async Task HttpSelection_ExportsOnlySelectedPreviewLine()
    {
        var harness = CreateOrderHarness(OrderType.Internal, 5);
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 1, QtyOrdered = 7 });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        var preview = await host.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/orders/10/marking/preview");
        var snapshot = preview.GetProperty("snapshot").GetString();
        var response = await host.Client.PostAsJsonAsync("/api/orders/10/marking/export", new { selected_line_ids = new[] { 101L }, snapshot });
        Assert.True(response.IsSuccessStatusCode);
        await AssertWorkbookQty(response, 7);
        var invalid = await host.Client.PostAsJsonAsync("/api/orders/10/marking/export", new { selected_line_ids = Array.Empty<long>(), snapshot });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Theory]
    [InlineData(OrderType.Internal, OrderStatus.Draft)]
    [InlineData(OrderType.Customer, OrderStatus.InProgress)]
    [InlineData(OrderType.Customer, OrderStatus.Accepted)]
    public async Task Export_RetryProducesSameRowsWithoutAnyWrites(OrderType type, OrderStatus status)
    {
        var harness = CreateOrderHarness(type, 6000, status);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());
        var original = harness.GetOrder(10);
        var before = harness.TransactionExecutionCount;
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var response = await host.Client.PostAsync("/api/orders/10/marking/export", null);
            Assert.True(response.IsSuccessStatusCode);
            await AssertWorkbookQty(response, 6000);
        }
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(harness.GetOrder(10)));
        Assert.Equal(before, harness.TransactionExecutionCount);
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Merged)]
    public void TerminalOrders_CannotExport(OrderStatus status)
    {
        var harness = CreateOrderHarness(OrderType.Customer, 10, status);
        var service = new OrderMarkingExportService(harness.Store);
        Assert.False(service.Preview(10).IsSuccess);
        Assert.False(service.Export(10, DateTime.UtcNow).IsSuccess);
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CustomerOrderExport_UsesShortageAfterReservedStock()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 7200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-3600",
            qty: 3600,
            prdDocId: 900);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        await AssertWorkbookQty(response, 3600);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Fact]
    public async Task CustomerOrderExport_FullyCoveredByReservedStock_CreatesNoCodesAndMarksCompleted()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 7200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-7200",
            qty: 7200,
            prdDocId: 901);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
        Assert.False(harness.GetOrder(10).MarkingCompleted);
    }

    [Fact]
    public void OrderBasedExportSummary_IncludesAllMarkableOrderLines()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 10);
        for (var index = 2; index <= 4; index++)
        {
            harness.SeedItem(CreateMarkableItem(index));
            harness.SeedOrderLine(new OrderLine
            {
                Id = 99 + index,
                OrderId = 10,
                ItemId = index,
                QtyOrdered = 10
            });
        }
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар 1",
            QtyPlanned = 10,
            ToLocationId = 1
        });

        var result = new OrderMarkingExportService(harness.Store)
            .Export(10, new DateTime(2026, 5, 8, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.LineCount);
        Assert.Equal(4, result.Lines.Count);
        Assert.Contains(result.Lines, line => line.OrderLineId == 100 && line.ExportQty == 10);
    }

    [Fact]
    public async Task CustomerOrderExport_M1_ReservedFilledHu_ReducesExportQty()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-100",
            qty: 100,
            prdDocId: 902);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        await AssertWorkbookQty(response, 100);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Fact]
    public async Task CustomerOrderExport_M2_SecondExport_IsIdempotentWithoutNewCodes()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 100,
            itemId: 1,
            locationId: 1,
            huCode: "HU-RES-100",
            qty: 100,
            prdDocId: 903);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var first = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);
        var codeCountAfterFirst = harness.MarkingCodes.Count;

        var second = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
        Assert.Equal(codeCountAfterFirst, harness.MarkingCodes.Count);
    }

    [Fact]
    public async Task CustomerOrderExport_M3_ReservedOnOtherLine_DoesNotReduceTargetLineShortage()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        harness.SeedItem(CreateMarkableItem(2));
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 2,
            QtyOrdered = 50
        });
        SeedFilledHuReservation(
            harness,
            orderId: 10,
            orderLineId: 101,
            itemId: 2,
            locationId: 1,
            huCode: "HU-OTHER-ITEM",
            qty: 50,
            prdDocId: 904);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        await AssertWorkbookQty(response, 200);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Fact]
    public async Task CustomerOrderExport_M4_WarehouseBoundHu_ReducesProductionMarkingShortage()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 100,
            ToLocationId = 1,
            ToHu = "HU-PLANNED-ONLY"
        });
        harness.SeedDoc(new Doc
        {
            Id = 905,
            DocRef = "PRD-PLANNED",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 11,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9051,
            PrdDocId = 905,
            DocLineId = 1,
            OrderId = 11,
            ItemId = 1,
            HuCode = "HU-PLANNED-ONLY",
            PlannedQty = 100,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(1, 1, 100, "HU-PLANNED-ONLY");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        await AssertWorkbookQty(response, 100);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
    }

    [Fact]
    public async Task CustomerOrderExport_WarehouseBoundPlusProductionPallet_ExportsOnlyProductionQty()
    {
        var harness = CreateOrderHarness(OrderType.Customer, qty: 1200);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            ItemName = "Маркируемый товар",
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-WH-600"
        });
        harness.SeedBalance(1, 1, 600, "HU-WH-600");
        harness.SeedDoc(new Doc
        {
            Id = 920,
            DocRef = "PRD-CUSTOMER",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 9201,
            PrdDocId = 920,
            DocLineId = 92001,
            OrderId = 10,
            OrderLineId = 100,
            ItemId = 1,
            HuCode = "HU-PRD-600",
            PlannedQty = 600,
            ToLocationId = 1,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        var response = await host.Client.PostAsync("/api/orders/10/marking/export", content: null);

        Assert.True(response.IsSuccessStatusCode);
        await AssertWorkbookQty(response, 600);
        Assert.Empty(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);
        Assert.False(harness.GetOrder(10).MarkingCompleted);
        Assert.Equal(MarkingStatus.NotRequired, harness.GetOrder(10).EffectiveMarkingStatus);
        Assert.Empty(harness.GetOrder(10).MarkingLabel);
    }

    [Fact]
    public void PostgresAddMarkingCodes_UsesOneBulkCommandWithoutConflictSuppression()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
        var start = source.IndexOf(
            "public void AddMarkingCodes(IReadOnlyList<MarkingCode> codes)",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public int CountMarkingCodesByMarkingOrder",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("FROM unnest(", method);
        Assert.DoesNotContain("ON CONFLICT", method, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreach (var code in codes)", method);
        Assert.Equal(1, CountOccurrences(method, "ExecuteNonQuery()"));
    }

    private static async Task AssertWorkbookQty(HttpResponseMessage response, double expected)
    {
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new System.IO.Compression.ZipArchive(stream);
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = System.Xml.Linq.XDocument.Load(sheet);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = xml.Descendants(ns + "row").SelectMany(row => row.Elements(ns + "c")).ToArray();
        Assert.Equal(0, cells.Length % 3);
        var qty = cells.Where(cell => cell.Attribute("r")!.Value.StartsWith("C"))
            .Sum(cell => double.Parse(cell.Element(ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, qty);
    }

    private static void SeedFilledHuReservation(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long itemId,
        long locationId,
        string huCode,
        double qty,
        long prdDocId)
    {
        harness.SeedOrderReceiptPlanLines(orderId, new OrderReceiptPlanLine
        {
            Id = prdDocId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = itemId,
            ItemName = $"Маркируемый товар {itemId}",
            QtyPlanned = qty,
            ToLocationId = locationId,
            ToHu = huCode
        });
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = $"PRD-{prdDocId}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 11,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = prdDocId * 10,
            PrdDocId = prdDocId,
            DocLineId = 1,
            OrderId = 11,
            ItemId = itemId,
            HuCode = huCode,
            PlannedQty = qty,
            ToLocationId = locationId,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(itemId, locationId, qty, huCode);
    }

    private static CloseDocumentHarness CreateOrderHarness(
        OrderType orderType,
        double qty,
        OrderStatus status = OrderStatus.Draft)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "01", Name = "Склад 01" });
        harness.SeedItem(CreateMarkableItem(1));
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = orderType == OrderType.Internal ? "INT-010" : "CO-010",
            Type = orderType,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = MarkingStatus.NotRequired
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 100,
            OrderId = 10,
            ItemId = 1,
            QtyOrdered = qty
        });
        return harness;
    }

    private static Item CreateMarkableItem(long id)
    {
        return new Item
        {
            Id = id,
            Name = $"Маркируемый товар {id}",
            Gtin = $"0460123456789{id % 10}",
            ItemTypeEnableMarking = true
        };
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private sealed class OrderMarkingExportPayload
    {
        [JsonPropertyName("created_code_qty")]
        public double CreatedCodeQty { get; init; }
    }
}
