using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Maintenance;

[Collection("MaintenanceBackfillEndpointTests")]
public sealed class OrderReservationBackfillEndpointTests
{
    [Fact]
    public async Task DryRun_ReturnsReport_AndDoesNotChangePlanLines()
    {
        var scenario = BackfillEndpointScenario.Build();
        scenario.AddProductionReceiptWithoutOrder(itemId: 100, huCode: "HU-1", qty: 10);
        scenario.AddCustomerOrder(orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);
        await using var host = await MaintenanceBackfillHttpHost.StartAsync(scenario.Store.Object);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/maintenance/backfill-reservations/dry-run",
            new { });
        using var payload = await ReadJsonAsync(response, HttpStatusCode.OK);
        var root = payload.RootElement;

        Assert.Equal("DRY_RUN", root.GetProperty("mode").GetString());
        Assert.Equal(1, root.GetProperty("orders_with_changes").GetInt32());
        Assert.Empty(scenario.Plans[10]);
        Assert.Equal(scenario.LedgerRows, root.GetProperty("ledger_rows_before").GetInt64());
        Assert.Equal(scenario.LedgerRows, root.GetProperty("ledger_rows_after").GetInt64());
    }

    [Fact]
    public async Task Apply_WithoutConfirm_ReturnsBadRequest_AndDoesNotChangePlanLines()
    {
        var scenario = BackfillEndpointScenario.Build();
        scenario.AddProductionReceiptWithoutOrder(itemId: 100, huCode: "HU-1", qty: 10);
        scenario.AddCustomerOrder(orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);
        await using var host = await MaintenanceBackfillHttpHost.StartAsync(scenario.Store.Object);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/maintenance/backfill-reservations/apply",
            new { confirm = "" });
        var result = await response.Content.ReadFromJsonAsync<ApiResult>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal("CONFIRM_REQUIRED", result.Error);
        Assert.Empty(scenario.Plans[10]);
        scenario.Store.Verify(store => store.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()), Times.Never);
        scenario.Store.Verify(store => store.CountLedgerEntries(), Times.Never);
    }

    [Fact]
    public async Task Apply_WithConfirm_ChangesOnlyOrderReceiptPlanLines()
    {
        var scenario = BackfillEndpointScenario.Build();
        scenario.AddProductionReceiptWithoutOrder(itemId: 100, huCode: "HU-1", qty: 10);
        scenario.AddCustomerOrder(orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);
        var docCountBefore = scenario.Docs.Count;
        var docLineCountBefore = scenario.DocLines.Values.Sum(lines => lines.Count);
        var ledgerRowsBefore = scenario.LedgerRows;
        await using var host = await MaintenanceBackfillHttpHost.StartAsync(scenario.Store.Object);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/admin/maintenance/backfill-reservations/apply",
            new { confirm = "APPLY" });
        using var payload = await ReadJsonAsync(response, HttpStatusCode.OK);
        var root = payload.RootElement;

        Assert.Equal("APPLY", root.GetProperty("mode").GetString());
        var planLine = Assert.Single(scenario.Plans[10]);
        Assert.Equal("HU-1", planLine.ToHu);
        Assert.Equal(10, planLine.QtyPlanned);
        Assert.Equal(docCountBefore, scenario.Docs.Count);
        Assert.Equal(docLineCountBefore, scenario.DocLines.Values.Sum(lines => lines.Count));
        Assert.Equal(ledgerRowsBefore, scenario.LedgerRows);
        Assert.Equal(ledgerRowsBefore, root.GetProperty("ledger_rows_before").GetInt64());
        Assert.Equal(ledgerRowsBefore, root.GetProperty("ledger_rows_after").GetInt64());
        scenario.Store.Verify(store => store.AddLedgerEntry(It.IsAny<LedgerEntry>()), Times.Never);
    }

    [Fact]
    public async Task ParallelRun_ReturnsConflict()
    {
        var scenario = BackfillEndpointScenario.Build();
        scenario.AddProductionReceiptWithoutOrder(itemId: 100, huCode: "HU-1", qty: 10);
        scenario.AddCustomerOrder(orderId: 10, orderRef: "C-10", itemId: 100, qty: 10, useReservedStock: true);

        var transactionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work =>
            {
                transactionEntered.SetResult();
                releaseTransaction.Task.GetAwaiter().GetResult();
                work(scenario.Store.Object);
            });

        await using var host = await MaintenanceBackfillHttpHost.StartAsync(scenario.Store.Object);
        var firstRequest = host.Client.PostAsJsonAsync(
            "/api/admin/maintenance/backfill-reservations/apply",
            new { confirm = "APPLY" });
        await transactionEntered.Task;

        using var conflictResponse = await host.Client.PostAsJsonAsync(
            "/api/admin/maintenance/backfill-reservations/dry-run",
            new { });
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<ApiResult>();

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.NotNull(conflict);
        Assert.False(conflict!.Ok);
        Assert.Equal("BACKFILL_ALREADY_RUNNING", conflict.Error);

        releaseTransaction.SetResult();
        using var firstResponse = await firstRequest;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }
}

[CollectionDefinition("MaintenanceBackfillEndpointTests", DisableParallelization = true)]
public sealed class MaintenanceBackfillEndpointTestCollection;

internal sealed class MaintenanceBackfillHttpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MaintenanceBackfillHttpHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<MaintenanceBackfillHttpHost> StartAsync(IDataStore store)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MaintenanceBackfillEndpoints).Assembly.FullName,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(store);

        var app = builder.Build();
        MaintenanceBackfillEndpoints.Map(app);
        await app.StartAsync();

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.SingleOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            await app.StopAsync();
            await app.DisposeAsync();
            throw new InvalidOperationException("HTTP test host did not expose a listening address.");
        }

        return new MaintenanceBackfillHttpHost(
            app,
            new HttpClient { BaseAddress = new Uri(address, UriKind.Absolute) });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class BackfillEndpointScenario
{
    public Mock<IDataStore> Store { get; } = new(MockBehavior.Strict);
    public Dictionary<long, Order> Orders { get; } = new();
    public Dictionary<long, List<OrderLine>> OrderLines { get; } = new();
    public Dictionary<long, List<OrderReceiptPlanLine>> Plans { get; } = new();
    public List<Doc> Docs { get; } = new();
    public Dictionary<long, IReadOnlyList<DocLine>> DocLines { get; } = new();
    public List<HuStockRow> HuStockRows { get; } = new();
    public Dictionary<long, Item> Items { get; } = new();
    public Dictionary<long, ItemType> ItemTypes { get; } = new();
    public Dictionary<long, double> ShippedByOrderLine { get; } = new();
    public long LedgerRows { get; set; } = 3;

    public static BackfillEndpointScenario Build()
    {
        var scenario = new BackfillEndpointScenario();
        scenario.Store.Setup(store => store.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(scenario.Store.Object));
        scenario.Store.Setup(store => store.CountLedgerEntries()).Returns(() => scenario.LedgerRows);
        scenario.Store.Setup(store => store.GetOrders())
            .Returns(() => scenario.Orders.Values.OrderBy(order => order.Id).ToArray());
        scenario.Store.Setup(store => store.GetOrderLines(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.OrderLines.TryGetValue(orderId, out var lines)
                ? lines.ToArray()
                : Array.Empty<OrderLine>());
        scenario.Store.Setup(store => store.GetShippedTotalsByOrderLine(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.OrderLines.TryGetValue(orderId, out var lines)
                ? scenario.ShippedByOrderLine
                    .Where(pair => lines.Any(line => line.Id == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<long, double>());
        scenario.Store.Setup(store => store.GetOrderReceiptRemaining(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.OrderLines.TryGetValue(orderId, out var lines)
                ? lines.Select(line => new OrderReceiptLine
                    {
                        OrderLineId = line.Id,
                        OrderId = orderId,
                        ItemId = line.ItemId,
                        ItemName = "Item",
                        QtyOrdered = line.QtyOrdered,
                        QtyReceived = 0,
                        QtyRemaining = line.QtyOrdered
                    })
                    .ToArray()
                : Array.Empty<OrderReceiptLine>());
        scenario.Store.Setup(store => store.GetOrderReceiptPlanLines(It.IsAny<long>()))
            .Returns<long>(orderId => scenario.Plans.TryGetValue(orderId, out var lines)
                ? lines.Select(ClonePlanLine).ToArray()
                : Array.Empty<OrderReceiptPlanLine>());
        scenario.Store.Setup(store => store.ReplaceOrderReceiptPlanLines(It.IsAny<long>(), It.IsAny<IReadOnlyList<OrderReceiptPlanLine>>()))
            .Callback<long, IReadOnlyList<OrderReceiptPlanLine>>((orderId, lines) =>
            {
                scenario.Plans[orderId] = lines.Select(ClonePlanLine).ToList();
            });
        scenario.Store.Setup(store => store.GetDocs()).Returns(() => scenario.Docs.ToArray());
        scenario.Store.Setup(store => store.GetDocLines(It.IsAny<long>()))
            .Returns<long>(docId => scenario.DocLines.TryGetValue(docId, out var lines)
                ? lines.ToArray()
                : Array.Empty<DocLine>());
        scenario.Store.Setup(store => store.GetHuStockRows()).Returns(() => scenario.HuStockRows.ToArray());
        scenario.Store.Setup(store => store.FindItemById(It.IsAny<long>()))
            .Returns<long>(itemId => scenario.Items.TryGetValue(itemId, out var item) ? item : null);
        scenario.Store.Setup(store => store.GetItemType(It.IsAny<long>()))
            .Returns<long>(typeId => scenario.ItemTypes.TryGetValue(typeId, out var itemType) ? itemType : null);
        return scenario;
    }

    public void AddProductionReceiptWithoutOrder(long itemId, string huCode, double qty)
    {
        var docId = 2000 + Docs.Count;
        Docs.Add(new Doc
        {
            Id = docId,
            DocRef = $"PRD-{docId}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            CreatedAt = new DateTime(2026, 1, 1),
            ClosedAt = new DateTime(2026, 1, 1, 1, 0, 0)
        });
        DocLines[docId] =
        [
            new DocLine
            {
                Id = docId + 10000,
                DocId = docId,
                ItemId = itemId,
                Qty = qty,
                ToLocationId = 5,
                ToHu = huCode
            }
        ];
        HuStockRows.Add(new HuStockRow
        {
            HuCode = huCode,
            ItemId = itemId,
            LocationId = 5,
            Qty = qty
        });
    }

    public void AddCustomerOrder(long orderId, string orderRef, long itemId, double qty, bool useReservedStock)
    {
        AddItem(itemId, enableOrderReservation: true);
        Orders[orderId] = new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = useReservedStock,
            CreatedAt = new DateTime(2026, 1, 2).AddMinutes(orderId)
        };
        OrderLines[orderId] = [];
        AddCustomerOrderLine(orderId, itemId, qty);
        Plans[orderId] = [];
    }

    private void AddCustomerOrderLine(long orderId, long itemId, double qty)
    {
        AddItem(itemId, enableOrderReservation: true);
        OrderLines[orderId].Add(new OrderLine
        {
            Id = orderId * 100 + OrderLines[orderId].Count,
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = qty
        });
    }

    private void AddItem(long itemId, bool enableOrderReservation)
    {
        var typeId = enableOrderReservation ? 50 : 51;
        ItemTypes[typeId] = new ItemType
        {
            Id = typeId,
            Name = enableOrderReservation ? "Reserved type" : "Plain type",
            EnableOrderReservation = enableOrderReservation
        };
        Items[itemId] = new Item
        {
            Id = itemId,
            Name = "Item",
            ItemTypeId = typeId
        };
    }

    private static OrderReceiptPlanLine ClonePlanLine(OrderReceiptPlanLine line)
    {
        return new OrderReceiptPlanLine
        {
            Id = line.Id,
            OrderId = line.OrderId,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            QtyPlanned = line.QtyPlanned,
            ToLocationId = line.ToLocationId,
            ToLocationCode = line.ToLocationCode,
            ToLocationName = line.ToLocationName,
            ToHu = line.ToHu,
            SortOrder = line.SortOrder
        };
    }
}
