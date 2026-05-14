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
                    item_id = 1001,
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
    public async Task CreateMarkingFromProductionNeeds_CreatesMarkingForCurrentNeed()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(4800, payload.CreatedQty);
        Assert.Equal(4800, Assert.Single(harness.MarkingOrders).RequestedQuantity);
        Assert.DoesNotContain(harness.Store.GetOrders(), order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_RequiresExplicitMarkingCreation()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var orderPayload = await CreateOrdersAsync(host.Client);

        Assert.True(orderPayload.Ok);
        Assert.Equal(1, orderPayload.InternalDraftCount);
        Assert.Empty(harness.MarkingOrders);

        var markingPayload = await CreateMarkingAsync(host.Client);

        Assert.True(markingPayload.Ok);
        Assert.Equal(1, markingPayload.CreatedTaskCount);
        Assert.Equal(3600, markingPayload.CreatedQty);
        var markingOrder = Assert.Single(harness.MarkingOrders);
        Assert.Null(markingOrder.OrderId);
        Assert.Equal(MarkingNeedCreationService.ProductionNeedSourceType, markingOrder.SourceType);
        Assert.Equal(3600, markingOrder.RequestedQuantity);
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_SecondPost_DoesNotDuplicateActiveTask()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var firstPayload = await CreateMarkingAsync(host.Client);
        var secondPayload = await CreateMarkingAsync(host.Client);

        Assert.True(firstPayload.Ok);
        Assert.Equal(1, firstPayload.CreatedTaskCount);
        Assert.Equal(4800, firstPayload.CreatedQty);
        Assert.True(secondPayload.Ok);
        Assert.Equal(0, secondPayload.CreatedTaskCount);
        Assert.Equal(0, secondPayload.CreatedQty);
        Assert.Single(harness.MarkingOrders);
        Assert.Equal(4800, Assert.Single(harness.MarkingOrders).RequestedQuantity);
    }

    [Fact]
    public async Task CreateOrdersFromProductionNeed_BeforeManualMarking_OrderDtoShowsMarkingNotCompleted()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        var json = SerializeOrderDto(new OrderService(harness.Store).GetOrder(internalDraft.Id));

        Assert.True(json.GetProperty("marking_applies").GetBoolean());
        Assert.False(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("REQUIRED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка не проведена", json.GetProperty("marking_label").GetString());
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_WithoutCodes_OrderDtoRemainsNotCompleted()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);
        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Single(harness.MarkingOrders);
        Assert.Empty(harness.MarkingCodes);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        var json = SerializeOrderDto(new OrderService(harness.Store).GetOrder(internalDraft.Id));

        Assert.False(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("REQUIRED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка не проведена", json.GetProperty("marking_label").GetString());
    }

    [Fact]
    public async Task ExportMarkingExcel_CreatesSyntheticCodes_AndOrderDtoShowsCompleted()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);
        await CreateMarkingAsync(host.Client);
        var task = Assert.Single(harness.MarkingOrders);

        var export = new MarkingExcelService(harness.Store).Export(new[] { task.Id }, Array.Empty<long>(), new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(export.IsSuccess);
        Assert.True(harness.MarkingCodes.Count >= task.RequestedQuantity);

        var internalDraft = Assert.Single(harness.Store.GetOrders().Where(order => order.Type == OrderType.Internal && order.Status == OrderStatus.Draft));
        var json = SerializeOrderDto(new OrderService(harness.Store).GetOrder(internalDraft.Id));

        Assert.True(json.GetProperty("marking_completed").GetBoolean());
        Assert.Equal("PRINTED", json.GetProperty("marking_effective_status").GetString());
        Assert.Equal("Маркировка проведена", json.GetProperty("marking_label").GetString());
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_IncludesOpenInternalDraft_WhenMinStockNeedWasReduced()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
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
            ItemId = 1001,
            QtyOrdered = 3600,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        var needRow = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true));
        Assert.Equal(1200, needRow.ToCloseOrdersQty);
        Assert.Equal(0, needRow.ToMinStockQty);
        Assert.Equal(1200, needRow.TotalToMakeQty);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(3600, payload.CreatedQty);
        Assert.Equal(3600, Assert.Single(harness.MarkingOrders).RequestedQuantity);
        Assert.Contains(payload.DebugSummary, line => line.Contains("production_need_qty=1200", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("open_internal_remaining_qty=3600", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("required_marking_qty=3600", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_WithReservedProducedStock_BeforeCreateOrders_CreatesOnlyCurrentNeed()
    {
        var (harness, apiStore) = CreateReservedMarkedCustomerNeedScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var needRow = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: false));
        Assert.Equal(3600, needRow.TotalToMakeQty);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(3600, payload.CreatedQty);
        Assert.Equal(3600, Assert.Single(harness.MarkingOrders).RequestedQuantity);
        Assert.Contains(payload.DebugSummary, line => line.Contains("production_need_qty=3600", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("open_internal_remaining_qty=0", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("required_marking_qty=3600", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_AfterCreateOrders_DoesNotDoubleCountReportAndDraft()
    {
        var (harness, apiStore) = CreateReservedStockWithMinStockMarkingScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var createOrdersPayload = await CreateOrdersAsync(host.Client);
        Assert.True(createOrdersPayload.Ok);
        Assert.Equal(1, createOrdersPayload.InternalDraftCount);
        Assert.Equal(3600, createOrdersPayload.CreatedQty);

        var needRow = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: false));
        Assert.Equal(3600, needRow.TotalToMakeQty);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(3600, payload.CreatedQty);
        Assert.Equal(3600, Assert.Single(harness.MarkingOrders).RequestedQuantity);
        Assert.Contains(payload.DebugSummary, line => line.Contains("production_need_qty=3600", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("open_internal_remaining_qty=3600", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("required_marking_qty=3600", StringComparison.Ordinal));
        Assert.Contains(payload.DebugSummary, line => line.Contains("created_qty=3600", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_OldCompletedGlobalMarking_DoesNotBlockCurrentNeed()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600);
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 10000,
            RequestNumber = "OLD-10000",
            Status = MarkingOrderStatus.Completed,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(4800, payload.CreatedQty);
        Assert.Equal(2, harness.MarkingOrders.Count);
        Assert.Contains(harness.MarkingOrders, order =>
            order.SourceType == MarkingNeedCreationService.ProductionNeedSourceType
            && order.RequestedQuantity == 4800);
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_BoundCodesDoNotCoverNewNeed()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 600, minStockQty: 0);
        var oldTaskId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = oldTaskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 600,
            RequestNumber = "OLD-BOUND-600",
            Status = MarkingOrderStatus.Printed,
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedMarkingCodes(oldTaskId, count: 600, gtin: "04607186951520", receiptLineId: 5000);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(600, payload.CreatedQty);
        Assert.Equal(2, harness.MarkingOrders.Count);
        Assert.Contains(harness.MarkingOrders, order =>
            order.Id != oldTaskId
            && order.SourceType == MarkingNeedCreationService.ProductionNeedSourceType
            && order.RequestedQuantity == 600);
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_FreeCodesDoNotReduceRequestedQuantity()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 3600, minStockQty: 0);
        var oldTaskId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = oldTaskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 600,
            RequestNumber = "OLD-FREE-600",
            Status = MarkingOrderStatus.Printed,
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedMarkingCodes(oldTaskId, count: 600, gtin: "04607186951520");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(3600, payload.CreatedQty);
        Assert.Equal(2, harness.MarkingOrders.Count);
        Assert.Contains(harness.MarkingOrders, order =>
            order.Id != oldTaskId
            && order.SourceType == MarkingNeedCreationService.ProductionNeedSourceType
            && order.RequestedQuantity == 3600);
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_FreeCodesCoverCurrentNeed_AfterExport_DoesNotCreateNewTask()
    {
        var (harness, apiStore) = CreateReservedStockWithMinStockMarkingScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        await CreateOrdersAsync(host.Client);
        var firstPayload = await CreateMarkingAsync(host.Client);

        Assert.True(firstPayload.Ok);
        Assert.Equal(1, firstPayload.CreatedTaskCount);
        Assert.Equal(3600, firstPayload.CreatedQty);

        var task = Assert.Single(harness.MarkingOrders);
        var export = new MarkingExcelService(harness.Store).Export(new[] { task.Id }, Array.Empty<long>(), new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
        Assert.True(export.IsSuccess);

        var secondPayload = await CreateMarkingAsync(host.Client);

        Assert.True(secondPayload.Ok);
        Assert.Equal(0, secondPayload.CreatedTaskCount);
        Assert.Equal(0, secondPayload.CreatedQty);
        Assert.Single(harness.MarkingOrders);
        Assert.Contains(secondPayload.DebugSummary, line => line.Contains("free_code_qty=3600", StringComparison.Ordinal));
        Assert.Contains(secondPayload.DebugSummary, line => line.Contains("created_qty=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_AfterReceiptBoundCodes_CreatesNewTask()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 600, minStockQty: 0);
        var oldTaskId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = oldTaskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 600,
            RequestNumber = "OLD-RECEIPT-600",
            Status = MarkingOrderStatus.Printed,
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedMarkingCodes(oldTaskId, count: 600, gtin: "04607186951520", receiptLineId: 6000);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(1, payload.CreatedTaskCount);
        Assert.Equal(600, payload.CreatedQty);
        Assert.Equal(2, harness.MarkingOrders.Count);
    }

    [Fact]
    public async Task CreateMarkingFromProductionNeeds_SkipsNonMarkableItems()
    {
        var (harness, apiStore) = CreateMarkingNeedScenario(customerQty: 1200, minStockQty: 3600, enableMarking: false);
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);

        var payload = await CreateMarkingAsync(host.Client);

        Assert.True(payload.Ok);
        Assert.Equal(0, payload.CreatedTaskCount);
        Assert.Empty(harness.MarkingOrders);
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
            "Строка 1 (Горчица): требуется 1134 код(ов) КМ, привязано 0, доступно свободных 0.",
            result.Errors);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(50).Status);
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

    private static async Task<CreateMarkingFromProductionNeedsResponse> CreateMarkingAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/marking/create-from-production-needs", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CreateMarkingFromProductionNeedsResponse>();
        return Assert.IsType<CreateMarkingFromProductionNeedsResponse>(payload);
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

    private sealed class CreateMarkingFromProductionNeedsResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("created_task_count")]
        public int CreatedTaskCount { get; init; }

        [JsonPropertyName("created_qty")]
        public double CreatedQty { get; init; }

        [JsonPropertyName("debug_summary")]
        public IReadOnlyList<string> DebugSummary { get; init; } = Array.Empty<string>();
    }
}
