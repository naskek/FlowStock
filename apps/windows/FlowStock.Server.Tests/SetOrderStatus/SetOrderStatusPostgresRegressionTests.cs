using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Data;
using FlowStock.Server;
using FlowStock.Server.Tests.SetOrderStatus.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace FlowStock.Server.Tests.SetOrderStatus;

public sealed class SetOrderStatusPostgresRegressionTests
{
    [Theory]
    [InlineData(ProductionPalletStatus.Cancelled)]
    [InlineData(ProductionPalletStatus.Planned)]
    public async Task CancelInternalOrder_WithRemovableDraftPrdPalletDocLineTails_Succeeds(string palletStatus)
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            var fixture = SeedInternalOrderWithDraftProductionReceiptPallet(scopedStore, palletStatus);

            await using var host = await PostgresOrderStatusHost.StartAsync(scopedStore);
            var payload = await SetOrderStatusHttpApi.ChangeAsync(host.Client, fixture.OrderId, "CANCELLED");

            Assert.True(payload.Ok);
            Assert.Equal(OrderStatus.Cancelled, scopedStore.GetOrder(fixture.OrderId)?.Status);
            Assert.Null(scopedStore.GetDoc(fixture.PrdDocId));
            Assert.Empty(scopedStore.GetDocLines(fixture.PrdDocId));
            Assert.Empty(scopedStore.GetProductionPalletsByDoc(fixture.PrdDocId));
            AssertNoProductionPalletLinesWithNullDocLineId(connectionString);
        });
    }

    [Fact]
    public async Task CancelInternalOrder_WithMixedProductionPalletLines_SucceedsWithoutOrphans()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            var fixture = SeedInternalOrderWithDraftMixedProductionPallet(scopedStore);
            var mixed = Assert.Single(scopedStore.GetProductionPalletsByDoc(fixture.PrdDocId));
            Assert.True(mixed.IsMixedPallet);
            Assert.Equal(2, mixed.Lines.Count);
            Assert.All(mixed.Lines, line => Assert.True(line.DocLineId > 0));

            await using var host = await PostgresOrderStatusHost.StartAsync(scopedStore);
            var payload = await SetOrderStatusHttpApi.ChangeAsync(host.Client, fixture.OrderId, "CANCELLED");

            Assert.True(payload.Ok);
            Assert.Equal(OrderStatus.Cancelled, scopedStore.GetOrder(fixture.OrderId)?.Status);
            Assert.Null(scopedStore.GetDoc(fixture.PrdDocId));
            Assert.Empty(scopedStore.GetDocLines(fixture.PrdDocId));
            Assert.Empty(scopedStore.GetProductionPalletsByDoc(fixture.PrdDocId));
            AssertNoProductionPalletLinesWithNullDocLineId(connectionString);
        });
    }

    [Fact]
    public async Task CancelInternalOrder_WithFilledDraftPrdPallet_ReturnsBusinessBadRequest()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            var fixture = SeedInternalOrderWithDraftProductionReceiptPallet(scopedStore, ProductionPalletStatus.Filled);

            await using var host = await PostgresOrderStatusHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsJsonAsync(
                $"/api/orders/{fixture.OrderId}/status",
                new SetOrderStatusRequest { Status = "CANCELLED" });

            var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
            Assert.False(payload.Ok);
            Assert.Equal("ORDER_CANCEL_PRD_HAS_PALLET_FACTS", payload.Error);
            Assert.Equal(OrderStatus.InProgress, scopedStore.GetOrder(fixture.OrderId)?.Status);
            Assert.NotNull(scopedStore.GetDoc(fixture.PrdDocId));
        });
    }

    [Fact]
    public async Task CancelInternalOrder_WithDraftPrdLedgerRows_ReturnsBusinessBadRequest()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            var fixture = SeedInternalOrderWithDraftProductionReceiptPallet(scopedStore, ProductionPalletStatus.Planned);
            scopedStore.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = DateTime.UtcNow,
                DocId = fixture.PrdDocId,
                ItemId = fixture.ItemId,
                LocationId = fixture.LocationId,
                QtyDelta = 600,
                HuCode = fixture.HuCode
            });

            await using var host = await PostgresOrderStatusHost.StartAsync(scopedStore);
            using var response = await host.Client.PostAsJsonAsync(
                $"/api/orders/{fixture.OrderId}/status",
                new SetOrderStatusRequest { Status = "CANCELLED" });

            var payload = await SetOrderStatusHttpApi.ReadApiResultAsync(response, HttpStatusCode.BadRequest);
            Assert.False(payload.Ok);
            Assert.Equal("ORDER_CANCEL_PRD_HAS_LEDGER", payload.Error);
            Assert.Equal(OrderStatus.InProgress, scopedStore.GetOrder(fixture.OrderId)?.Status);
            Assert.NotNull(scopedStore.GetDoc(fixture.PrdDocId));
        });
    }

    private static DraftPrdPalletFixture SeedInternalOrderWithDraftProductionReceiptPallet(
        IDataStore store,
        string palletStatus)
    {
        EnsureAtLeastOneLocation(store);
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var locationId = store.GetLocations().First().Id;
        var itemId = store.AddItem(new Item
        {
            Name = $"Тестовый полуфабрикат {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        var orderRef = $"T-CAN-{suffix[^6..]}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        var huCode = store.CreateProductionPalletHuCode("SET-ORDER-STATUS-REGRESSION");
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PRD-T-{suffix[^6..]}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            OrderId = orderId,
            OrderRef = orderRef
        });
        var docLineId = store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = itemId,
            Qty = 600,
            ToLocationId = locationId,
            ToHu = huCode
        });

        var pallet = Assert.Single(store.PlanProductionPallets(docId, DateTime.UtcNow));
        Assert.Equal(docLineId, pallet.DocLineId);
        Assert.Equal(orderLineId, pallet.OrderLineId);
        Assert.Equal(orderLineId, Assert.Single(pallet.Lines).OrderLineId);

        if (string.Equals(palletStatus, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(1, store.CancelProductionPallets([pallet.Id]));
        }
        else if (string.Equals(palletStatus, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            store.MarkProductionPalletFilled(pallet.Id, DateTime.UtcNow, "TEST-DEVICE");
        }
        else
        {
            Assert.Equal(ProductionPalletStatus.Planned, palletStatus);
        }

        pallet = Assert.Single(store.GetProductionPalletsByDoc(docId));
        Assert.Equal(palletStatus, pallet.Status);
        Assert.Equal(docLineId, pallet.DocLineId);
        Assert.Equal(orderLineId, Assert.Single(pallet.Lines).OrderLineId);

        return new DraftPrdPalletFixture(orderId, docId, itemId, locationId, huCode);
    }

    private static DraftPrdPalletFixture SeedInternalOrderWithDraftMixedProductionPallet(IDataStore store)
    {
        EnsureAtLeastOneLocation(store);
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var locationId = store.GetLocations().First().Id;
        var firstItemId = store.AddItem(new Item { Name = $"Тестовый mixed компонент A {suffix}", BaseUom = "шт" });
        var secondItemId = store.AddItem(new Item { Name = $"Тестовый mixed компонент B {suffix}", BaseUom = "шт" });
        var orderRef = $"T-MIX-{suffix[^6..]}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        var firstOrderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = firstItemId,
            QtyOrdered = 200,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ProductionPalletGroup = "MIX-CANCEL"
        });
        var secondOrderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = secondItemId,
            QtyOrdered = 300,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ProductionPalletGroup = "MIX-CANCEL"
        });
        var huCode = store.CreateProductionPalletHuCode("SET-ORDER-STATUS-MIXED-REGRESSION");
        var docId = store.AddDoc(new Doc
        {
            DocRef = $"PRD-MIX-{suffix[^6..]}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            OrderId = orderId,
            OrderRef = orderRef
        });
        store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = firstOrderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = firstItemId,
            Qty = 200,
            ToLocationId = locationId,
            ToHu = huCode,
            PackSingleHu = true
        });
        store.AddDocLine(new DocLine
        {
            DocId = docId,
            OrderLineId = secondOrderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = secondItemId,
            Qty = 300,
            ToLocationId = locationId,
            ToHu = huCode,
            PackSingleHu = true
        });

        var pallet = Assert.Single(store.PlanProductionPallets(docId, DateTime.UtcNow));
        Assert.True(pallet.IsMixedPallet);
        Assert.Equal(2, pallet.Lines.Count);
        Assert.All(pallet.Lines, line => Assert.True(line.DocLineId > 0));
        return new DraftPrdPalletFixture(orderId, docId, firstItemId, locationId, huCode);
    }

    private static void AssertNoProductionPalletLinesWithNullDocLineId(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM production_pallet_lines WHERE doc_line_id IS NULL;";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    private static void EnsureAtLeastOneLocation(IDataStore store)
    {
        if (store.GetLocations().Count > 0)
        {
            return;
        }

        store.AddLocation(new Location
        {
            Code = "FG",
            Name = "Готовая продукция",
            AutoHuDistributionEnabled = true
        });
    }

    private static async Task RunInRollbackTransactionAsync(string connectionString, Func<IDataStore, Task> work)
    {
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var exception = await Record.ExceptionAsync(() =>
        {
            store.ExecuteInTransaction(scopedStore =>
            {
                work(scopedStore).GetAwaiter().GetResult();
                throw new RollbackRequestedException();
            });
            return Task.CompletedTask;
        });

        if (exception is RollbackRequestedException)
        {
            return;
        }

        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        Assert.Fail("Rollback transaction did not request rollback.");
    }

    private static string? ResolvePostgresTestConnectionString()
    {
        foreach (var key in new[]
                 {
                     "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
                     "FLOWSTOCK_POSTGRES_CONNECTION",
                     "POSTGRES_CONNECTION_STRING"
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        const string fallback =
            "Host=127.0.0.1;Port=5432;Database=flowstock;Username=flowstock;Password=flowstock;Pooling=false;Timeout=2;Command Timeout=30";
        try
        {
            var store = new PostgresDataStore(fallback);
            store.Initialize();
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private sealed record DraftPrdPalletFixture(
        long OrderId,
        long PrdDocId,
        long ItemId,
        long LocationId,
        string HuCode);

    private sealed class RollbackRequestedException : Exception;

    private sealed class PostgresOrderStatusHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PostgresOrderStatusHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<PostgresOrderStatusHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderStatusEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(typeof(IDataStore), store);

            var app = builder.Build();
            OrderStatusEndpoint.Map(app);
            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.Single();
            if (string.IsNullOrWhiteSpace(address))
            {
                await app.StopAsync();
                await app.DisposeAsync();
                throw new InvalidOperationException("HTTP test host did not expose a listening address.");
            }

            return new PostgresOrderStatusHost(
                app,
                new HttpClient
                {
                    BaseAddress = new Uri(address, UriKind.Absolute)
                });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

