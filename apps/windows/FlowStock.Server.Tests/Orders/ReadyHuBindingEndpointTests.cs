using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class ReadyHuBindingEndpointTests
{
    [Fact]
    public async Task ReadyHuBindingEndpoint_ReturnsComputedReadModel()
    {
        var harness = CreateHarnessWithReadyHu();
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/orders/hu-bindings/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("READY_HU_BINDING_AVAILABLE", document.RootElement.GetProperty("request_type").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("hu_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("order_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("line_count").GetInt32());
        Assert.Equal("HU-READY", document.RootElement.GetProperty("hu_rows").EnumerateArray().Single().GetProperty("hu_code").GetString());
    }

    [Fact]
    public async Task RequestsSummary_IncludesReadyHuBindingPending()
    {
        var harness = CreateHarnessWithReadyHu();
        var ledgerCountBefore = harness.LedgerEntries.Count;
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/requests/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(0, document.RootElement.GetProperty("item_requests_pending").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("order_requests_pending").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("ready_hu_binding_pending").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("action_required_count").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("business_notifications_unread").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("total_pending").GetInt32());
        Assert.Equal(ledgerCountBefore, harness.LedgerEntries.Count);
        harness.VerifyRequestsSummaryCountPathUsed(Times.Once());
        harness.VerifyRequestListsNotUsed();
        harness.VerifyReadyHuBindingSummaryPathUsed(Times.Once());
        harness.VerifyReadyHuBindingFullReadModelNotUsed();
    }

    [Fact]
    public async Task RequestsSummary_ReadyHuBindingPendingIsZeroWhenNoCompatibleFreeHuExists()
    {
        var harness = CreateHarness();
        var ledgerCountBefore = harness.LedgerEntries.Count;
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/requests/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(0, document.RootElement.GetProperty("item_requests_pending").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("order_requests_pending").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("ready_hu_binding_pending").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("action_required_count").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("business_notifications_unread").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("total_pending").GetInt32());
        Assert.Equal(ledgerCountBefore, harness.LedgerEntries.Count);
        harness.VerifyRequestsSummaryCountPathUsed(Times.Once());
        harness.VerifyRequestListsNotUsed();
        harness.VerifyReadyHuBindingSummaryPathUsed(Times.Once());
        harness.VerifyReadyHuBindingFullReadModelNotUsed();
    }

    [Fact]
    public async Task RequestsSummary_IncludesPendingItemAndOrderCountsWithoutMaterializingLists()
    {
        var harness = CreateHarness();
        harness.SeedItemRequest(new ItemRequest
        {
            Id = 1,
            Barcode = "460000000001",
            Comment = "missing item",
            CreatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            Status = "NEW"
        });
        harness.SeedItemRequest(new ItemRequest
        {
            Id = 2,
            Barcode = "460000000002",
            Comment = "resolved item",
            CreatedAt = new DateTime(2026, 5, 1, 12, 5, 0, DateTimeKind.Utc),
            Status = "RESOLVED",
            ResolvedAt = new DateTime(2026, 5, 1, 12, 10, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderRequest(new OrderRequest
        {
            Id = 10,
            RequestType = "CREATE_ORDER",
            PayloadJson = "{}",
            Status = OrderRequestStatus.Pending,
            CreatedAt = new DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderRequest(new OrderRequest
        {
            Id = 11,
            RequestType = "CREATE_ORDER",
            PayloadJson = "{}",
            Status = OrderRequestStatus.Approved,
            CreatedAt = new DateTime(2026, 5, 1, 13, 5, 0, DateTimeKind.Utc)
        });
        var ledgerCountBefore = harness.LedgerEntries.Count;
        await using var host = await ReadyHuBindingHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/requests/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(1, document.RootElement.GetProperty("item_requests_pending").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("order_requests_pending").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("ready_hu_binding_pending").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("action_required_count").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("business_notifications_unread").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("total_pending").GetInt32());
        Assert.Equal(ledgerCountBefore, harness.LedgerEntries.Count);
        harness.VerifyRequestsSummaryCountPathUsed(Times.Once());
        harness.VerifyRequestListsNotUsed();
        harness.VerifyReadyHuBindingSummaryPathUsed(Times.Once());
        harness.VerifyReadyHuBindingFullReadModelNotUsed();
    }

    [Fact]
    public void ProgramRequestsSummary_UsesReadyHuBindingPendingField()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Server", "Program.cs");
        var start = source.IndexOf("app.MapGet(\"/api/requests/summary\"", StringComparison.Ordinal);
        var end = source.IndexOf("app.MapPost(\"/api/orders/requests/{requestId:long}/resolve\"", start, StringComparison.Ordinal);
        var summaryEndpoint = source[start..end];

        Assert.Contains("ready_hu_binding_pending", summaryEndpoint);
        Assert.Contains("IRequestsSummaryStore", summaryEndpoint);
        Assert.Contains("CountPendingItemRequests()", summaryEndpoint);
        Assert.Contains("CountPendingOrderRequests()", summaryEndpoint);
        Assert.Contains("ReadyHuBindingEndpoint.CountPendingNotifications(store)", summaryEndpoint);
        Assert.Contains("total_pending = itemCount + orderCount + readyHuBindingPending", summaryEndpoint);
        Assert.DoesNotContain("GetItemRequests(false)", summaryEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrderRequests(false)", summaryEndpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestsSummaryCount_UsesCheapSummaryStoreInsteadOfFullReadyHuReadModel()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Server", "ReadyHuBindingEndpoint.cs");
        var start = source.IndexOf("public static int CountPendingNotifications", StringComparison.Ordinal);
        var end = source.IndexOf("public static ReadyHuBindingResponse", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("IReadyHuBindingSummaryStore", method);
        Assert.Contains("HasPendingReadyHuBinding()", method);
        Assert.DoesNotContain("ReadyHuBindingReadModelService", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".Build()", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyHuBindingReadModel_ReusesCandidatesServiceAndDoesNotWriteState()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Core", "Services", "ReadyHuBindingReadModelService.cs");

        Assert.Contains("new HuReservationCandidatesService(_dataStore).Build", source);
        Assert.Contains("IOptimizedHuReservationCandidatesStore", source);
        Assert.Contains("GetReservedOrderReceiptHuCodes(null)", source);
        Assert.DoesNotContain("ReplaceOrderReceiptPlanLines", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddLedgerEntry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelProductionPallet", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateOrder(", source, StringComparison.Ordinal);
    }

    private static CloseDocumentHarness CreateHarnessWithReadyHu()
    {
        var harness = CreateHarness();
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = ItemId,
            QtyOrdered = 600
        });
        harness.SeedBalance(ItemId, LocationId, 600, "HU-READY");
        return harness;
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = LocationId, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = ItemId, Name = "Товар", BaseUom = "шт" });
        return harness;
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

    private sealed class ReadyHuBindingHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ReadyHuBindingHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<ReadyHuBindingHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ReadyHuBindingEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            ReadyHuBindingEndpoint.Map(app);
            app.MapGet("/api/requests/summary", (IDataStore dataStore) =>
            {
                var summaryStore = dataStore as IRequestsSummaryStore;
                var itemCount = summaryStore?.CountPendingItemRequests() ?? 0;
                var orderCount = summaryStore?.CountPendingOrderRequests() ?? 0;
                var readyHuBindingPending = ReadyHuBindingEndpoint.CountPendingNotifications(dataStore);
                var businessNotificationsUnread = dataStore.CountUnreadBusinessNotifications(BusinessNotificationEndpoints.WpfReaderKey);
                return Results.Ok(new
                {
                    item_requests_pending = itemCount,
                    order_requests_pending = orderCount,
                    ready_hu_binding_pending = readyHuBindingPending,
                    action_required_count = itemCount + orderCount + readyHuBindingPending,
                    business_notifications_unread = businessNotificationsUnread,
                    total_pending = itemCount + orderCount + readyHuBindingPending + businessNotificationsUnread
                });
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new ReadyHuBindingHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private const long ItemId = 6;
    private const long LocationId = 1;
}
