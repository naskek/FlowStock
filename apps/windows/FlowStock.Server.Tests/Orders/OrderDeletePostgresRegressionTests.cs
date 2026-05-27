using System.Net;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using FlowStock.Server.Tests.UpdateOrder.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderDeletePostgresRegressionTests
{
    [Fact]
    public void DeleteOrderLine_CleansPalletPlanAndReservationChildrenBeforeOrderLineDelete()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodBody = SliceMethod(
            source,
            "private void DeleteOrderLinesCore(NpgsqlConnection connection, long orderId, IReadOnlyCollection<long> orderLineIds)",
            "private void EnsureOrderLinesCanBeDeleted");

        Assert.Contains("EnsureOrderLinesCanBeDeleted(connection, orderId, ids);", methodBody, StringComparison.Ordinal);
        Assert.Contains("ClearPlannedProductionPalletPlanForOrderLines(orderId, ids);", methodBody, StringComparison.Ordinal);
        AssertDeleteBefore(
            methodBody,
            "DELETE FROM order_receipt_plan_lines WHERE order_line_id = ANY(@order_line_ids)",
            "DELETE FROM order_lines WHERE id = ANY(@order_line_ids)");
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithPlannedPallets_DeletesLineWithoutFkFailure()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var palletService = new ProductionPalletService(scopedStore);
            var plan = palletService.PlanOrder(fixture.OrderId);
            Assert.Contains(
                scopedStore.GetProductionPalletsByDoc(plan.PrdDocId),
                pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                          || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            var payload = await UpdateOrderHttpApi.UpdateAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRequest(fixture));

            Assert.True(payload.Ok);
            Assert.Equal(1, payload.LineCount);

            var remainingLines = scopedStore.GetOrderLines(fixture.OrderId);
            Assert.Single(remainingLines);
            Assert.DoesNotContain(remainingLines, line => line.Id == fixture.DeletedOrderLineId);
            Assert.Contains(remainingLines, line => line.Id == fixture.RemainingOrderLineId);

            var palletsAfter = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId);
            Assert.DoesNotContain(
                palletsAfter,
                pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                          || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));
        });
    }

    [Fact]
    public async Task PutUpdate_RemovingCustomerLineWithFilledPallets_ReturnsBusinessBadRequest()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        await RunInRollbackTransactionAsync(connectionString, async scopedStore =>
        {
            EnsureAtLeastOneLocation(scopedStore);
            var fixture = SeedCustomerOrderWithTwoLines(scopedStore);
            var palletService = new ProductionPalletService(scopedStore);
            var plan = palletService.PlanOrder(fixture.OrderId);
            var palletToFill = scopedStore.GetProductionPalletsByDoc(plan.PrdDocId)
                .First(pallet => pallet.OrderLineId == fixture.DeletedOrderLineId
                                 || pallet.Lines.Any(line => line.OrderLineId == fixture.DeletedOrderLineId));

            scopedStore.MarkProductionPalletFilled(
                palletToFill.Id,
                new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc),
                "TEST-DEVICE");

            await using var host = await PostgresOrderUpdateHost.StartAsync(scopedStore);
            using var response = await UpdateOrderHttpApi.PutRawAsync(
                host.Client,
                fixture.OrderId,
                BuildDeleteFirstLineRawJson(fixture));

            var payload = await UpdateOrderHttpApi.ReadApiErrorResultAsync(response, HttpStatusCode.BadRequest);
            Assert.False(payload.Ok);
            Assert.Equal("ORDER_LINE_HAS_FILLED_PALLETS", payload.Error);
            Assert.Contains("нельзя удалить строку", payload.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var remainingLines = scopedStore.GetOrderLines(fixture.OrderId);
            Assert.Equal(2, remainingLines.Count);
            Assert.Contains(remainingLines, line => line.Id == fixture.DeletedOrderLineId);
        });
    }

    private static UpdateOrderHttpApi.UpdateOrderRequest BuildDeleteFirstLineRequest(CustomerOrderFixture fixture)
    {
        return new UpdateOrderHttpApi.UpdateOrderRequest
        {
            OrderRef = fixture.OrderRef,
            Type = "CUSTOMER",
            PartnerId = fixture.PartnerId,
            Status = "IN_PROGRESS",
            Lines =
            [
                new UpdateOrderHttpApi.UpdateOrderLineRequest
                {
                    OrderLineId = fixture.RemainingOrderLineId,
                    ItemId = fixture.RemainingItemId,
                    QtyOrdered = 600
                }
            ]
        };
    }

    private static string BuildDeleteFirstLineRawJson(CustomerOrderFixture fixture)
    {
        return $$"""
        {
          "order_ref": "{{fixture.OrderRef}}",
          "type": "CUSTOMER",
          "partner_id": {{fixture.PartnerId}},
          "status": "IN_PROGRESS",
          "lines": [
            {
              "order_line_id": {{fixture.RemainingOrderLineId}},
              "item_id": {{fixture.RemainingItemId}},
              "qty_ordered": 600
            }
          ]
        }
        """;
    }

    private static CustomerOrderFixture SeedCustomerOrderWithTwoLines(IDataStore store)
    {
        var suffix = DateTime.UtcNow.Ticks.ToString();
        var partnerId = store.AddPartner(new Partner
        {
            Name = $"Тестовый клиент {suffix}",
            Code = $"T-CL-{suffix}"
        });

        var deletedItemId = store.AddItem(new Item
        {
            Name = $"Тестовый товар A {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        var remainingItemId = store.AddItem(new Item
        {
            Name = $"Тестовый товар B {suffix}",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });

        var orderRef = $"T-DEL-{suffix[^6..]}";
        var orderId = store.AddOrder(new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        var deletedOrderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = deletedItemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        var remainingOrderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = remainingItemId,
            QtyOrdered = 600,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return new CustomerOrderFixture(
            orderId,
            orderRef,
            partnerId,
            deletedItemId,
            remainingItemId,
            deletedOrderLineId,
            remainingOrderLineId);
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

        Assert.IsType<RollbackRequestedException>(exception);
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

    private static void AssertDeleteBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Не найден фрагмент: {first}");
        Assert.True(secondIndex >= 0, $"Не найден фрагмент: {second}");
        Assert.True(firstIndex < secondIndex, $"Ожидалось, что '{first}' идет раньше '{second}'.");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");

        return source[start..end];
    }

    private static string GetPostgresDataStorePath()
        => GetRepoFilePath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

    private static string GetRepoFilePath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }

    private sealed record CustomerOrderFixture(
        long OrderId,
        string OrderRef,
        long PartnerId,
        long DeletedItemId,
        long RemainingItemId,
        long DeletedOrderLineId,
        long RemainingOrderLineId);

    private sealed class RollbackRequestedException : Exception;

    private sealed class PostgresOrderUpdateHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PostgresOrderUpdateHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<PostgresOrderUpdateHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderUpdateEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(typeof(IDataStore), store);

            var app = builder.Build();
            OrderUpdateEndpoint.Map(app);
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

            return new PostgresOrderUpdateHost(
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
