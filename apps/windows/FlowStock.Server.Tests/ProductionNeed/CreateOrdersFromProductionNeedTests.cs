using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.ProductionNeed;

[Collection("CreateOrder")]
public sealed class CreateOrdersFromProductionNeedTests
{
    [Fact]
    public async Task CreateOrdersFromProductionNeed_CreatesSingleInternalDraft_AndSecondCallDoesNotDuplicate()
    {
        var (harness, apiStore) = CreateMixedNeedScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.InternalDraftCount);
        Assert.Equal(0, payload.CustomerDraftCount);
        Assert.Equal(1, payload.CreatedLineCount);

        var draftOrders = harness.Store.GetOrders().Where(order => order.Status == OrderStatus.Draft).OrderBy(order => order.Id).ToArray();
        Assert.Single(draftOrders);
        Assert.DoesNotContain(draftOrders, order => order.Type == OrderType.Customer);

        var internalDraft = Assert.Single(draftOrders.Where(order => order.Type == OrderType.Internal));
        Assert.Null(internalDraft.PartnerId);
        var internalDraftLine = Assert.Single(harness.GetOrderLines(internalDraft.Id));
        Assert.Equal(1134, internalDraftLine.QtyOrdered);
        Assert.Equal(ProductionLinePurpose.InternalStock, internalDraftLine.ProductionPurpose);

        var needRow = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true));
        Assert.Equal(756, needRow.ToCloseOrdersQty);
        Assert.Equal(0, needRow.ToMinStockQty);
        Assert.Equal(756, needRow.TotalToMakeQty);

        var secondPayload = await CreateOrdersAsync(host.Client);
        Assert.True(secondPayload.Ok);
        Assert.Equal(0, secondPayload.CustomerDraftCount);
        Assert.Equal(0, secondPayload.InternalDraftCount);
        Assert.Equal(0, secondPayload.CreatedLineCount);
        Assert.Equal(2, harness.OrderCount);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_AfterNewCustomerDemand_DoesNotCreateInternalDraftForCustomerPart()
    {
        var (harness, apiStore) = CreateMixedNeedScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);

        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-002",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 5, 8),
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 7, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 100,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.CustomerDraftCount);
        Assert.Equal(0, payload.InternalDraftCount);
        Assert.Equal(0, payload.CreatedLineCount);

        var internalDrafts = harness.Store.GetOrders()
            .Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft)
            .OrderBy(order => order.Id)
            .ToArray();
        Assert.Single(internalDrafts);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_WithOnlyInternalNeed_CreatesOnlyInternalDraft()
    {
        var (harness, apiStore) = CreateInternalOnlyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.CustomerDraftCount);
        Assert.Equal(1, payload.InternalDraftCount);
        Assert.Equal(1, payload.CreatedLineCount);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Equal(500, Assert.Single(harness.GetOrderLines(internalDraft.Id)).QtyOrdered);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_WithOnlyCustomerNeed_DoesNotCreateInternalDraft()
    {
        var (harness, apiStore) = CreateCustomerOnlyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.CustomerDraftCount);
        Assert.Equal(0, payload.InternalDraftCount);
        Assert.Equal(0, payload.CreatedLineCount);
        Assert.DoesNotContain(harness.Store.GetOrders(), order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_CreatesDraftOnlyForMinStockPart()
    {
        var (harness, apiStore) = CreateTwoItemNeedScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.CustomerDraftCount);
        Assert.Equal(1, payload.InternalDraftCount);
        Assert.Equal(2, payload.CreatedLineCount);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        var linesByItem = harness.GetOrderLines(internalDraft.Id).ToDictionary(line => line.ItemId, line => line.QtyOrdered);
        Assert.Equal(3600, linesByItem[1001]);
        Assert.Equal(1134, linesByItem[1002]);

        var rows = new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true)
            .ToDictionary(row => row.ItemId);
        Assert.Equal(1200, rows[1001].ToCloseOrdersQty);
        Assert.Equal(0, rows[1001].ToMinStockQty);
        Assert.Equal(1200, rows[1001].TotalToMakeQty);
        Assert.Equal(756, rows[1002].ToCloseOrdersQty);
        Assert.Equal(0, rows[1002].ToMinStockQty);
        Assert.Equal(756, rows[1002].TotalToMakeQty);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_ForMarkableItem_CreatesInternalDraftThatRequiresKmOnReceipt()
    {
        var (harness, apiStore) = CreateMixedNeedScenario();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            ItemTypeEnableMarking = true,
            MinStockQty = 1134
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        var internalLine = Assert.Single(harness.GetOrderLines(internalDraft.Id));
        harness.SeedOrderReceiptRemaining(internalDraft.Id, new OrderReceiptLine
        {
            OrderLineId = internalLine.Id,
            OrderId = internalDraft.Id,
            ItemId = internalLine.ItemId,
            ItemName = "Горчица",
            QtyOrdered = internalLine.QtyOrdered,
            QtyReceived = 0,
            QtyRemaining = internalLine.QtyOrdered,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedDoc(new Doc
        {
            Id = 50,
            DocRef = "PRD-2026-000050",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = internalDraft.Id,
            OrderRef = internalDraft.OrderRef,
            CreatedAt = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 500,
            DocId = 50,
            OrderLineId = internalLine.Id,
            ItemId = internalLine.ItemId,
            Qty = internalLine.QtyOrdered,
            ToLocationId = 1,
            ToHu = "HU-PRD-050"
        });

        var result = harness.CreateService().TryCloseDoc(50, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(
            "Строка 1 (Горчица): требуется привязать 1134 код(ов) КМ, сейчас 0.",
            result.Errors);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(50).Status);
    }

    private static async Task<CreateProductionNeedOrdersResponse> CreateOrdersAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/production-needs/create-orders", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CreateProductionNeedOrdersResponse>();
        return Assert.IsType<CreateProductionNeedOrdersResponse>(payload);
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateMixedNeedScenario()
    {
        var harness = CreateBaseHarness();
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 0);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 5, 7),
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 756,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return (harness, new InMemoryApiDocStore());
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateInternalOnlyScenario()
    {
        var harness = CreateBaseHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = false,
            MinStockQty = 0
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 0);
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 500
        });
        harness.SeedBalance(itemId: 1002, locationId: 1, qty: 0);
        return (harness, new InMemoryApiDocStore());
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateCustomerOnlyScenario()
    {
        var harness = CreateBaseHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = false,
            MinStockQty = 0
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 0);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 5, 7),
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 756,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return (harness, new InMemoryApiDocStore());
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateTwoItemNeedScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = false
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 7, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951544",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 3600
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Хрен",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 1134
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 0);
        harness.SeedBalance(itemId: 1002, locationId: 1, qty: 0);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 5, 7),
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 1200,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 1002,
            QtyOrdered = 756,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return (harness, new InMemoryApiDocStore());
    }

    private static CloseDocumentHarness CreateBaseHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = false
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 7, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 1134
        });
        return harness;
    }

    private sealed class CreateProductionNeedOrdersResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("customer_draft_count")]
        public int CustomerDraftCount { get; init; }

        [JsonPropertyName("internal_draft_count")]
        public int InternalDraftCount { get; init; }

        [JsonPropertyName("created_line_count")]
        public int CreatedLineCount { get; init; }
    }
}
