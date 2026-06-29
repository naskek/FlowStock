using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowStock.Server.Tests.Catalog;

public sealed class ItemStorageConditionsPostgresTests
{
    [Fact]
    public async Task PostgresDataStore_StorageConditions_RoundTripsThroughItemReadsAndUpdates()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        AssertStorageConditionsColumnExists(connectionString);

        await RunInRollbackTransactionAsync(connectionString, scopedStore =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var expected = "от 0С до +10С  влажность 75%\nне замораживать + беречь";
            var itemId = SeedItem(scopedStore, suffix, " \r\n" + expected + " \t ");

            Assert.Equal(expected, scopedStore.FindItemById(itemId)!.StorageConditions);
            Assert.Equal(expected, scopedStore.FindItemByBarcode($"SC-PG-{suffix}")!.StorageConditions);
            Assert.Equal(expected, scopedStore.FindItemByGtin(BuildGtin(suffix))!.StorageConditions);

            var listed = Assert.Single(scopedStore.GetItems(suffix).Where(item => item.Id == itemId));
            Assert.Equal(expected, listed.StorageConditions);

            var updated = new Item
            {
                Id = itemId,
                Name = $"Storage update {suffix}",
                IsActive = false,
                Barcode = $"SC-PG-UPD-{suffix}",
                Gtin = BuildGtin("1" + suffix[1..]),
                BaseUom = "кор",
                DefaultPackagingId = listed.DefaultPackagingId,
                Brand = "Марка  + %",
                Volume = "500 мл",
                ShelfLifeMonths = 18,
                StorageConditions = " \tновое значение\nс двумя  пробелами + 0С% ",
                MaxQtyPerHu = 42,
                TaraId = listed.TaraId,
                IsMarked = true,
                ItemTypeId = listed.ItemTypeId,
                MinStockQty = 7.5
            };

            scopedStore.UpdateItem(updated);

            var afterUpdate = scopedStore.FindItemById(itemId)!;
            Assert.Equal("новое значение\nс двумя  пробелами + 0С%", afterUpdate.StorageConditions);
            AssertItemFields(updated, afterUpdate);

            scopedStore.UpdateItem(new Item
            {
                Id = itemId,
                Name = afterUpdate.Name,
                IsActive = afterUpdate.IsActive,
                Barcode = afterUpdate.Barcode,
                Gtin = afterUpdate.Gtin,
                BaseUom = afterUpdate.BaseUom,
                DefaultPackagingId = afterUpdate.DefaultPackagingId,
                Brand = afterUpdate.Brand,
                Volume = afterUpdate.Volume,
                ShelfLifeMonths = afterUpdate.ShelfLifeMonths,
                StorageConditions = " \r\n\t ",
                MaxQtyPerHu = afterUpdate.MaxQtyPerHu,
                TaraId = afterUpdate.TaraId,
                IsMarked = afterUpdate.IsMarked,
                ItemTypeId = afterUpdate.ItemTypeId,
                MinStockQty = afterUpdate.MinStockQty
            });

            Assert.Null(scopedStore.FindItemById(itemId)!.StorageConditions);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ItemHttpApi_StorageConditions_CreateReadUpdateAndClear()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        AssertStorageConditionsColumnExists(connectionString);

        await using var host = await CatalogItemApiHost.StartAsync(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var barcode = $"SC-API-{suffix}";
        var gtin = BuildGtin(suffix);
        long? itemId = null;

        ExceptionDispatchInfo? testException = null;
        try
        {
            var createValue = "  хранить при 0С  +10С\nвлажность 75%  ";
            using var createResponse = await host.Client.PostAsJsonAsync("/api/items", new
            {
                name = $"Storage API {suffix}",
                barcode,
                gtin,
                base_uom = "шт",
                brand = "API brand",
                volume = "1 л",
                shelf_life_months = 12,
                is_active = true,
                storage_conditions = createValue
            });
            Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
            itemId = (await ReadJson(createResponse)).RootElement.GetProperty("item_id").GetInt64();

            var expectedCreateValue = "хранить при 0С  +10С\nвлажность 75%";
            using var listResponse = await host.Client.GetAsync($"/api/items?q={Uri.EscapeDataString(suffix)}");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            using (var listJson = await ReadJson(listResponse))
            {
                var listed = Assert.Single(listJson.RootElement.EnumerateArray(), item => item.GetProperty("id").GetInt64() == itemId.Value);
                Assert.Equal(expectedCreateValue, listed.GetProperty("storage_conditions").GetString());
            }

            using (var byBarcode = await GetJson(host.Client, $"/api/items/by-barcode/{Uri.EscapeDataString(barcode)}"))
            {
                Assert.Equal(expectedCreateValue, byBarcode.RootElement.GetProperty("storage_conditions").GetString());
            }

            var updateValue = " \tновые условия\n+5С  и 80% ";
            using var updateResponse = await host.Client.PostAsJsonAsync($"/api/items/{itemId.Value}", new
            {
                name = $"Storage API updated {suffix}",
                barcode,
                gtin,
                base_uom = "шт",
                brand = "API brand 2",
                volume = "2 л",
                shelf_life_months = 6,
                is_active = true,
                storage_conditions = updateValue
            });
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

            using (var afterUpdate = await GetJson(host.Client, $"/api/items/by-barcode/{Uri.EscapeDataString(barcode)}"))
            {
                Assert.Equal("новые условия\n+5С  и 80%", afterUpdate.RootElement.GetProperty("storage_conditions").GetString());
            }

            using var whitespaceResponse = await host.Client.PostAsJsonAsync($"/api/items/{itemId.Value}", new
            {
                name = $"Storage API updated {suffix}",
                barcode,
                gtin,
                base_uom = "шт",
                is_active = true,
                storage_conditions = " \r\n\t "
            });
            Assert.Equal(HttpStatusCode.OK, whitespaceResponse.StatusCode);

            using (var afterWhitespace = await GetJson(host.Client, $"/api/items/by-barcode/{Uri.EscapeDataString(barcode)}"))
            {
                Assert.True(afterWhitespace.RootElement.GetProperty("storage_conditions").ValueKind == JsonValueKind.Null);
            }

            using var nullResponse = await host.Client.PostAsJsonAsync($"/api/items/{itemId.Value}", new
            {
                name = $"Storage API updated {suffix}",
                barcode,
                gtin,
                base_uom = "шт",
                is_active = true,
                storage_conditions = (string?)null
            });
            Assert.Equal(HttpStatusCode.OK, nullResponse.StatusCode);

            using (var afterNull = await GetJson(host.Client, $"/api/items/by-barcode/{Uri.EscapeDataString(barcode)}"))
            {
                Assert.True(afterNull.RootElement.GetProperty("storage_conditions").ValueKind == JsonValueKind.Null);
            }
        }
        catch (Exception ex)
        {
            testException = ExceptionDispatchInfo.Capture(ex);
        }

        Exception? cleanupException = null;
        try
        {
            CleanupItem(connectionString, itemId, barcode);
        }
        catch (Exception ex)
        {
            cleanupException = ex;
        }

        if (testException != null && cleanupException != null)
        {
            throw new AggregateException(
                "HTTP storage conditions test failed and cleanup also failed.",
                testException.SourceException,
                cleanupException);
        }

        if (cleanupException != null)
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }

        testException?.Throw();
    }

    private static long SeedItem(IDataStore store, string suffix, string? storageConditions)
    {
        var taraId = store.AddTara(new Tara { Name = $"SC тара {suffix}" });
        var itemTypeId = store.AddItemType(new ItemType
        {
            Name = $"SC тип {suffix}",
            IsActive = true,
            IsVisibleInProductCatalog = true,
            EnableMinStockControl = true,
            EnableHuDistribution = true,
            EnableMarking = true
        });

        return store.AddItem(new Item
        {
            Name = $"Storage PG {suffix}",
            IsActive = true,
            Barcode = $"SC-PG-{suffix}",
            Gtin = BuildGtin(suffix),
            BaseUom = "шт",
            Brand = "Марка  0С",
            Volume = "250 мл",
            ShelfLifeMonths = 9,
            StorageConditions = storageConditions,
            MaxQtyPerHu = 24,
            TaraId = taraId,
            IsMarked = true,
            ItemTypeId = itemTypeId,
            MinStockQty = 3.5
        });
    }

    private static void AssertItemFields(Item expected, Item actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.IsActive, actual.IsActive);
        Assert.Equal(expected.Barcode, actual.Barcode);
        Assert.Equal(expected.Gtin, actual.Gtin);
        Assert.Equal(expected.BaseUom, actual.BaseUom);
        Assert.Equal(expected.DefaultPackagingId, actual.DefaultPackagingId);
        Assert.Equal(expected.Brand, actual.Brand);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ShelfLifeMonths, actual.ShelfLifeMonths);
        Assert.Equal(expected.MaxQtyPerHu, actual.MaxQtyPerHu);
        Assert.Equal(expected.TaraId, actual.TaraId);
        Assert.Equal(expected.IsMarked, actual.IsMarked);
        Assert.Equal(expected.ItemTypeId, actual.ItemTypeId);
        Assert.Equal(expected.MinStockQty, actual.MinStockQty);
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

    private static string ResolveRequiredPostgresTestConnectionString()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        Assert.True(
            connectionString != null,
            "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION or run local Docker PostgreSQL on 127.0.0.1:5432.");
        return connectionString;
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

    private static void AssertStorageConditionsColumnExists(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT data_type
FROM information_schema.columns
WHERE table_schema = current_schema()
  AND table_name = 'items'
  AND column_name = 'storage_conditions';";
        var dataType = command.ExecuteScalar() as string;
        Assert.Equal("text", dataType);
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static void CleanupItem(string connectionString, long? itemId, string barcode)
    {
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var idsToDelete = new HashSet<long>();
        if (itemId.HasValue)
        {
            idsToDelete.Add(itemId.Value);
        }

        var itemByBarcode = store.FindItemByBarcode(barcode);
        if (itemByBarcode != null)
        {
            idsToDelete.Add(itemByBarcode.Id);
        }

        foreach (var id in idsToDelete)
        {
            store.DeleteItem(id);
        }

        if (itemId.HasValue)
        {
            Assert.Null(store.FindItemById(itemId.Value));
        }

        Assert.Null(store.FindItemByBarcode(barcode));
    }

    private static string BuildGtin(string suffix)
    {
        var digits = new string(suffix.Where(char.IsDigit).ToArray());
        if (digits.Length < 11)
        {
            digits = (digits + "01234567890")[..11];
        }

        return "046" + digits[..11];
    }

    private sealed class CatalogItemApiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private CatalogItemApiHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<CatalogItemApiHost> StartAsync(string connectionString)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<PostgresDataStore>(_ =>
            {
                var store = new PostgresDataStore(connectionString);
                store.Initialize();
                return store;
            });
            builder.Services.AddSingleton<IDataStore>(sp => sp.GetRequiredService<PostgresDataStore>());
            builder.Services.AddSingleton<CatalogService>();

            var app = builder.Build();
            ItemCatalogEndpoints.Map(app, connectionString);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses
                .Single();

            return new CatalogItemApiHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class RollbackRequestedException : Exception
    {
    }
}
