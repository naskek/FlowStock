using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Server;
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
        Assert.Equal(1134, payload.CreatedQty);

        var draftOrders = harness.Store.GetOrders().Where(order => order.Status == OrderStatus.Draft).OrderBy(order => order.Id).ToArray();
        Assert.Single(draftOrders);
        Assert.DoesNotContain(draftOrders, order => order.Type == OrderType.Customer);

        var internalDraft = Assert.Single(draftOrders.Where(order => order.Type == OrderType.Internal));
        Assert.Null(internalDraft.PartnerId);
        var internalDraftLine = Assert.Single(harness.GetOrderLines(internalDraft.Id));
        Assert.Equal(1134, internalDraftLine.QtyOrdered);
        Assert.Equal(ProductionLinePurpose.InternalStock, internalDraftLine.ProductionPurpose);
        Assert.Empty(harness.MarkingOrders);

        var needRow = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true));
        Assert.Equal(756, needRow.ToCloseOrdersQty);
        Assert.Equal(0, needRow.ToMinStockQty);
        Assert.Equal(756, needRow.TotalToMakeQty);

        var secondPayload = await CreateOrdersAsync(host.Client);
        Assert.True(secondPayload.Ok);
        Assert.Equal(0, secondPayload.CustomerDraftCount);
        Assert.Equal(0, secondPayload.InternalDraftCount);
        Assert.Equal(0, secondPayload.CreatedLineCount);
        Assert.Equal(0, secondPayload.CreatedQty);
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
        Assert.Empty(harness.MarkingOrders);

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
        Assert.Empty(harness.MarkingOrders);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Equal(500, Assert.Single(harness.GetOrderLines(internalDraft.Id)).QtyOrdered);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_UsesEditedPreviewQty_WhenProvidedByClient()
    {
        var (harness, apiStore) = CreateInternalOnlyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client, new
        {
            rows = new[]
            {
                new
                {
                    item_id = 1002,
                    qty_ordered = 125d
                }
            }
        });

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.InternalDraftCount);
        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Equal(125, Assert.Single(harness.GetOrderLines(internalDraft.Id)).QtyOrdered);
    }

    [Fact]
    public async Task ProductionNeedPreview_ReturnsOnlyCreatableStockRows()
    {
        var (harness, apiStore) = CreateTwoItemNeedScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/reports/production-need/create-orders/preview", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(payload.GetProperty("ok").GetBoolean());
        var rows = payload.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.True(row.GetProperty("qty_to_create").GetDouble() > 0));
        Assert.DoesNotContain(rows, row => row.GetProperty("item_id").GetInt64() == 999999);
    }

    [Fact]
    public async Task ProductionNeedPreview_WithOnlyCustomerDemandAndNoMinStock_ReturnsNoRows()
    {
        var (harness, apiStore) = CreateCustomerOnlyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/reports/production-need/create-orders/preview", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Empty(payload.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task ProductionNeedPreview_WithMinStockGap_ReturnsQtyToMinimum()
    {
        var (harness, apiStore) = CreateMinStockGapScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/reports/production-need/create-orders/preview", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(payload.GetProperty("rows").EnumerateArray());

        Assert.Equal(34, row.GetProperty("item_id").GetInt64());
        Assert.Equal(1824, row.GetProperty("qty_to_create").GetDouble());
    }

    [Fact]
    public async Task ProductionNeedPreview_WithOpenInternalPlan_ReducesQtyToCreate()
    {
        var (harness, apiStore) = CreateMinStockGapScenario();
        harness.SeedOrder(new Order
        {
            Id = 30,
            OrderRef = "030",
            Type = OrderType.Internal,
            Status = OrderStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 301,
            OrderId = 30,
            ItemId = 34,
            QtyOrdered = 1000,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/reports/production-need/create-orders/preview", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(payload.GetProperty("rows").EnumerateArray());

        Assert.Equal(824, row.GetProperty("qty_to_create").GetDouble());
    }

    [Fact]
    public async Task CreateProductionNeedOrders_ReturnsOkTrue_WhenInternalOrderCreated()
    {
        var (harness, apiStore) = CreateMinStockGapScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var previewResponse = await host.Client.PostAsJsonAsync("/api/reports/production-need/create-orders/preview", new { });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var previewRow = Assert.Single(preview.GetProperty("rows").EnumerateArray());

        var payload = await CreateOrdersAsync(host.Client, new
        {
            rows = new[]
            {
                new
                {
                    item_id = 34,
                    qty_ordered = 1824d
                }
            }
        });

        Assert.True(preview.GetProperty("ok").GetBoolean());
        Assert.Equal(34, previewRow.GetProperty("item_id").GetInt64());
        Assert.Equal(1824, previewRow.GetProperty("qty_to_create").GetDouble());
        Assert.True(payload.Ok);
        Assert.Equal(1, payload.InternalDraftCount);
        Assert.Equal(1, payload.CreatedLineCount);
        Assert.Equal(1824, payload.CreatedQty);
        var internalDraft = Assert.Single(harness.Store.GetOrders()
            .Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        var line = Assert.Single(harness.GetOrderLines(internalDraft.Id));
        Assert.Equal(34, line.ItemId);
        Assert.Equal(1824, line.QtyOrdered);
        Assert.Equal(0, harness.DocCount);
        Assert.Equal(0, harness.TotalDocLineCount);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task CreateProductionNeedOrders_DoesNotMutate_WhenReturningOkFalse()
    {
        var (harness, apiStore) = CreateMinStockGapScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/production-needs/create-orders", new
        {
            rows = new[]
            {
                new
                {
                    item_id = 34,
                    qty_ordered = 1825d
                }
            }
        });
        var payload = await response.Content.ReadFromJsonAsync<ApiResult>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(payload);
        Assert.False(payload.Ok);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);
        Assert.Equal(0, harness.DocCount);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public async Task CreateProductionNeedOrders_DoesNotCreateDuplicate_WhenOpenInternalAlreadyCoversNeed()
    {
        var (harness, apiStore) = CreateMinStockGapScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var first = await CreateOrdersAsync(host.Client, new
        {
            rows = new[]
            {
                new
                {
                    item_id = 34,
                    qty_ordered = 1824d
                }
            }
        });
        using var secondResponse = await host.Client.PostAsJsonAsync("/api/production-needs/create-orders", new
        {
            rows = new[]
            {
                new
                {
                    item_id = 34,
                    qty_ordered = 1824d
                }
            }
        });
        var second = await secondResponse.Content.ReadFromJsonAsync<ApiResult>();

        Assert.True(first.Ok);
        Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
        Assert.NotNull(second);
        Assert.False(second.Ok);
        var internalDraft = Assert.Single(harness.Store.GetOrders()
            .Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Single(harness.GetOrderLines(internalDraft.Id));
    }

    [Fact]
    public async Task PreviewAndCreate_Agree_OnRemainingMinStockNeed()
    {
        var (harness, apiStore) = CreateMinStockGapScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var previewResponse = await host.Client.PostAsJsonAsync("/api/reports/production-need/create-orders/preview", new { });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var previewRow = Assert.Single(preview.GetProperty("rows").EnumerateArray());

        var payload = await CreateOrdersAsync(host.Client, new
        {
            rows = new[]
            {
                new
                {
                    item_id = previewRow.GetProperty("item_id").GetInt64(),
                    qty_ordered = previewRow.GetProperty("qty_to_create").GetDouble()
                }
            }
        });
        var needRow = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true));

        Assert.True(payload.Ok);
        Assert.Equal(1824, payload.CreatedQty);
        Assert.Equal(34, needRow.ItemId);
        Assert.Equal(1824, needRow.OpenInternalOrderQty);
        Assert.Equal(0, needRow.QtyToCreate);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_RejectsEmptyAndZeroQuantities()
    {
        var (harness, apiStore) = CreateInternalOnlyScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var response = await host.Client.PostAsJsonAsync("/api/production-needs/create-orders", new
        {
            rows = new[]
            {
                new
                {
                    item_id = 1002,
                    qty_ordered = 0d
                }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult>();
        Assert.Equal("Нет строк с количеством больше нуля для создания внутреннего заказа.", payload?.Error);
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
        Assert.Empty(harness.MarkingOrders);
        Assert.DoesNotContain(harness.Store.GetOrders(), order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft);
    }

    [Fact]
    public void ProductionNeed_AfterInternalDraftCreated_KeepsRowVisible_WithOpenInternalQty()
    {
        var (harness, _) = CreateInternalOnlyScenario();

        var createResult = new ProductionNeedOrderCreationService(harness.Store).CreateDraftOrders();

        Assert.Equal(1, createResult.InternalDraftCount);
        var row = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: false));
        Assert.Equal(0, row.TotalToMakeQty);
        Assert.Equal(500, row.OpenInternalOrderQty);
    }

    [Fact]
    public void InactiveDiagnosticRow_RemainsVisible_ButIsExcludedFromDraftPreview()
    {
        var (harness, _) = CreateInternalOnlyScenario();
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            IsActive = false,
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 500
        });

        var diagnostic = Assert.Single(
            new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: false),
            row => row.ItemId == 1002);
        var preview = new ProductionNeedOrderCreationService(harness.Store).PreviewDraftOrders();

        Assert.Equal(500, diagnostic.QtyToCreate);
        Assert.DoesNotContain(preview.Rows, row => row.ItemId == 1002);
        Assert.Equal(0, harness.OrderCount);
    }

    [Fact]
    public async Task ExplicitInactiveSelection_FailsWithStructuredCode_InSingleTransaction()
    {
        var (harness, apiStore) = CreateInternalOnlyScenario();
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            IsActive = false,
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 500
        });

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var response = await host.Client.PostAsJsonAsync(
            "/api/production-needs/create-orders",
            new
            {
                rows = new[] { new { item_id = 1002, qty_ordered = 500d } }
            });
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResult>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.Error);
        Assert.Equal(1, harness.TransactionExecutionCount);
        Assert.Equal(0, harness.OrderCount);
        Assert.Equal(0, harness.TotalOrderLineCount);
    }

    [Fact]
    public void ProductionNeed_WithOpenPalletWork_PopulatesFilledPalletProgress()
    {
        var harness = CreateBaseHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 1000
        });
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedOrder(new Order
        {
            Id = 60,
            OrderRef = "060",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 14, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 601,
            OrderId = 60,
            ItemId = 1001,
            QtyOrdered = 1000,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedDoc(new Doc
        {
            Id = 70,
            DocRef = "PRD-2026-000070",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 14, 10, 10, 0, DateTimeKind.Utc),
            OrderId = 60,
            OrderRef = "060"
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 701,
            PrdDocId = 70,
            DocLineId = 7001,
            OrderId = 60,
            OrderLineId = 601,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000701",
            PlannedQty = 400,
            Status = ProductionPalletStatus.Filled,
            PalletNo = 1,
            PalletCount = 2,
            CreatedAt = new DateTime(2026, 5, 14, 10, 15, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 702,
            PrdDocId = 70,
            DocLineId = 7002,
            OrderId = 60,
            OrderLineId = 601,
            ItemId = 1001,
            ItemName = "Горчица",
            HuCode = "HU-000702",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Planned,
            PalletNo = 2,
            PalletCount = 2,
            CreatedAt = new DateTime(2026, 5, 14, 10, 16, 0, DateTimeKind.Utc)
        });

        var row = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true));
        Assert.Equal(1000, row.PlannedPalletQty);
        Assert.Equal(400, row.FilledPalletQty);
        Assert.Equal(2, row.PlannedPalletCount);
        Assert.Equal(1, row.FilledPalletCount);
        Assert.Equal(600, row.RemainingPalletQty);
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
        Assert.Equal(4734, payload.CreatedQty);
        Assert.Empty(harness.MarkingOrders);

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
    public async Task CreateOrdersFromProductionNeed_WithCustomerAndMinStockNeed_CreatesDraftForMinStock_WithoutMarking()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.InternalDraftCount);
        Assert.Equal(1, payload.CreatedLineCount);
        Assert.Equal(3600, payload.CreatedQty);
        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Equal(3600, Assert.Single(harness.GetOrderLines(internalDraft.Id)).QtyOrdered);
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_WithOnlyCustomerNeed_DoesNotCreateInternalDraft_OrMarking()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 0);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.InternalDraftCount);
        Assert.DoesNotContain(harness.Store.GetOrders(), order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft);
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_WithOnlyMinStockNeed_CreatesDraftWithoutMarking()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 0, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateOrdersAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.InternalDraftCount);
        Assert.Empty(harness.MarkingOrders);
        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Equal(3600, Assert.Single(harness.GetOrderLines(internalDraft.Id)).QtyOrdered);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_SecondClick_DoesNotDuplicateInternalDraft()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);
        var secondPayload = await CreateOrdersAsync(host.Client);

        Assert.True(secondPayload.Ok);
        Assert.Equal(0, secondPayload.InternalDraftCount);
        Assert.Equal(0, secondPayload.CreatedLineCount);
        Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_WithReservedMarkedStock_IgnoresStaleWebQty_AndRemainsIdempotent()
    {
        var (harness, apiStore) = CreateReservedMarkedCustomerNeedScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var freshNeed = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: false));
        Assert.Equal(3600, freshNeed.ToCloseOrdersQty);
        Assert.Equal(0, freshNeed.ToMinStockQty);
        Assert.Equal(3600, freshNeed.TotalToMakeQty);

        // Regression: Npgsql allows only one active reader/command per connection,
        // so GetStock must not run nested commands while its stock reader is still open.
        var payload = await CreateOrdersAsync(host.Client, new
        {
            rows = new[]
            {
                new
                {
                    item_id = 1001,
                    to_close_orders_qty = 7200,
                    to_min_stock_qty = 0,
                    total_to_make_qty = 7200
                }
            }
        });

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.InternalDraftCount);
        Assert.Equal(0, payload.CreatedLineCount);
        Assert.Equal(0, payload.CreatedQty);
        Assert.DoesNotContain(harness.Store.GetOrders(), order => order.Type == OrderType.Internal);
        Assert.Empty(harness.MarkingOrders);
        Assert.Contains(payload.DebugSummary, line => line.Contains("total_to_make=3600", StringComparison.Ordinal));

        var secondPayload = await CreateOrdersAsync(host.Client, new
        {
            rows = new[]
            {
                new
                {
                    item_id = 1001,
                    to_close_orders_qty = 7200,
                    to_min_stock_qty = 0,
                    total_to_make_qty = 7200
                }
            }
        });

        Assert.True(secondPayload.Ok);
        Assert.Equal(0, secondPayload.InternalDraftCount);
        Assert.Equal(0, secondPayload.CreatedLineCount);
        Assert.Equal(0, secondPayload.CreatedQty);
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_WpfAndWebLikeRequests_ReturnSameServerResult()
    {
        var (wpfHarness, wpfApiStore) = CreateReservedMarkedCustomerNeedScenario();
        await using var wpfHost = await CloseDocumentHttpHost.StartAsync(wpfHarness, wpfApiStore);

        var (webHarness, webApiStore) = CreateReservedMarkedCustomerNeedScenario();
        await using var webHost = await CloseDocumentHttpHost.StartAsync(webHarness, webApiStore);

        var wpfPayload = await CreateOrdersAsync(wpfHost.Client);
        var webPayload = await CreateOrdersAsync(webHost.Client, new
        {
            rows = new[]
            {
                new { item_id = 1001, total_to_make_qty = 7200 }
            }
        });

        Assert.Equal(wpfPayload.InternalDraftCount, webPayload.InternalDraftCount);
        Assert.Equal(wpfPayload.CreatedLineCount, webPayload.CreatedLineCount);
        Assert.Equal(wpfPayload.CreatedQty, webPayload.CreatedQty);
        Assert.Empty(wpfHarness.MarkingOrders);
        Assert.Empty(webHarness.MarkingOrders);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_ResponseIsProductionOnly()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        using var json = await CreateOrdersJsonAsync(host.Client);
        var root = json.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("internal_draft_count").GetInt32());
        Assert.Equal(1, root.GetProperty("created_line_count").GetInt32());
        Assert.Equal(3600, root.GetProperty("created_qty").GetDouble());
        Assert.False(root.TryGetProperty("created_marking_task_count", out _));
        Assert.False(root.TryGetProperty("created_marking_qty", out _));
        Assert.Empty(harness.MarkingOrders);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_ForMarkableItem_InternalReceiptClosesWithoutKmCodes()
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

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(50).Status);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_ForMarkableItem_WithEnoughKm_ClosesInternalReceipt()
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
        harness.SeedDoc(new Doc
        {
            Id = 51,
            DocRef = "PRD-2026-000051",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = internalDraft.Id,
            OrderRef = internalDraft.OrderRef,
            CreatedAt = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 501,
            DocId = 51,
            OrderLineId = internalLine.Id,
            ItemId = internalLine.ItemId,
            Qty = internalLine.QtyOrdered,
            ToLocationId = 1,
            ToHu = "HU-PRD-051"
        });
        harness.SeedKmCodeCountByReceiptLine(501, (int)internalLine.QtyOrdered);

        var result = harness.CreateService().TryCloseDoc(51, allowNegative: false);

        Assert.True(result.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(51).Status);
    }

    private static async Task<CreateProductionNeedOrdersResponse> CreateOrdersAsync(HttpClient client, object? body = null)
    {
        using var response = await client.PostAsJsonAsync("/api/production-needs/create-orders", body ?? new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CreateProductionNeedOrdersResponse>();
        return Assert.IsType<CreateProductionNeedOrdersResponse>(payload);
    }

    private static async Task<JsonDocument> CreateOrdersJsonAsync(HttpClient client, object? body = null)
    {
        using var response = await client.PostAsJsonAsync("/api/production-needs/create-orders", body ?? new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
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

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateMinStockGapScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = false
        });
        harness.SeedItem(new Item
        {
            Id = 34,
            Name = "Соус",
            Gtin = "04607186950034",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 5472
        });
        harness.SeedBalance(itemId: 34, locationId: 1, qty: 3648);
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

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateMarkingNeedScenario(
        double customerQty,
        double minStockQty,
        bool enableMarking = true)
    {
        var harness = CreateBaseHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = minStockQty > 0,
            ItemTypeEnableMarking = enableMarking,
            MinStockQty = minStockQty
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 0);
        if (customerQty > 0)
        {
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
                QtyOrdered = customerQty,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
        }

        return (harness, new InMemoryApiDocStore());
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateReservedMarkedCustomerNeedScenario()
    {
        var harness = CreateBaseHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = false,
            ItemTypeEnableMarking = true,
            MinStockQty = 0
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 3600);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-7200",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 5, 7),
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 7200,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(
            10,
            new OrderReceiptPlanLine
            {
                Id = 10001,
                OrderId = 10,
                OrderLineId = 101,
                ItemId = 1001,
                ItemName = "Горчица",
                QtyPlanned = 3600,
                ToLocationId = 1,
                ToLocationCode = "FG-01",
                SortOrder = 0
            });

        return (harness, new InMemoryApiDocStore());
    }

    private static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateReservedStockWithMinStockMarkingScenario()
    {
        var harness = CreateBaseHarness();
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            ItemTypeEnableMarking = true,
            MinStockQty = 3600
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 3600);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-7200",
            Type = OrderType.Customer,
            PartnerId = 200,
            DueDate = new DateTime(2026, 5, 7),
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = 7200,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(
            10,
            new OrderReceiptPlanLine
            {
                Id = 10001,
                OrderId = 10,
                OrderLineId = 101,
                ItemId = 1001,
                ItemName = "Горчица",
                QtyPlanned = 3600,
                ToLocationId = 1,
                ToLocationCode = "FG-01",
                SortOrder = 0
            });

        return (harness, new InMemoryApiDocStore());
    }

    private static JsonElement SerializeOrderDto(Order? order)
    {
        return JsonSerializer.SerializeToElement(OrderApiMapper.MapOrder(Assert.IsType<Order>(order)));
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

        [JsonPropertyName("created_qty")]
        public double CreatedQty { get; init; }

        [JsonPropertyName("debug_summary")]
        public IReadOnlyList<string> DebugSummary { get; init; } = Array.Empty<string>();
    }

}
