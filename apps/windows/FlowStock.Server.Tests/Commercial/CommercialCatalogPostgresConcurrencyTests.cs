using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialCatalogPostgresConcurrencyTests
{
    [Fact]
    public void Inactive_customer_price_can_be_deleted_without_changing_order_line_snapshots()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(store);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", $"PRICE-SNAPSHOT-{suffix}");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"PRICE-SNAPSHOT-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);
        var priceId = new PartnerItemSalePriceService(store).Create(
            partnerId,
            itemId,
            123.45m,
            isActive: false);
        var orderId = store.AddOrder(new Order
        {
            OrderRef = $"PRICE-SNAPSHOT-{suffix}",
            Type = OrderType.Customer,
            PartnerId = partnerId,
            Status = OrderStatus.Draft,
            CreatedAt = DateTime.Now
        });
        var orderLineId = store.AddOrderLine(new OrderLine
        {
            OrderId = orderId,
            ItemId = itemId,
            QtyOrdered = 2,
            UnitPriceGross = 123.45m,
            VatRate = 22m
        });

        new PartnerItemSalePriceService(store).Delete(priceId);

        Assert.Null(store.GetPartnerItemSalePrice(priceId));
        var line = Assert.Single(store.GetOrderLines(orderId), row => row.Id == orderLineId);
        Assert.Equal(123.45m, line.UnitPriceGross);
        Assert.Equal(22m, line.VatRate);
    }

    [Fact]
    public void Missing_customer_price_delete_returns_stable_error()
    {
        var store = new PostgresDataStore(ResolveRequiredPostgresTestConnectionString());

        var error = Assert.Throws<CommercialTermsException>(
            () => new PartnerItemSalePriceService(store).Delete(long.MaxValue));

        Assert.Equal("PARTNER_ITEM_SALE_PRICE_NOT_FOUND", error.ErrorCode);
    }

    [Fact]
    public async Task Deleting_customer_price_serializes_with_reactivation()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var setupStore = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(setupStore);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", $"PRICE-{suffix}");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"PRICE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);
        var priceId = new PartnerItemSalePriceService(setupStore).Create(
            partnerId,
            itemId,
            123.45m,
            isActive: false);

        using var reactivationWritten = new ManualResetEventSlim();
        using var allowReactivationCommit = new ManualResetEventSlim();
        var reactivationStore = new PostgresDataStore(
            WithApplicationName(connectionString, $"price-reactivate-{suffix}"));
        var reactivationTask = Task.Run(() => Record.Exception(() =>
            reactivationStore.ExecuteInTransaction(scopedStore =>
            {
                scopedStore.UpdatePartnerItemSalePrice(new PartnerItemSalePrice
                {
                    Id = priceId,
                    PartnerId = partnerId,
                    ItemId = itemId,
                    UnitPriceGross = 123.45m,
                    IsActive = true
                });
                reactivationWritten.Set();
                Assert.True(allowReactivationCommit.Wait(TimeSpan.FromSeconds(10)));
            })));

        Assert.True(reactivationWritten.Wait(TimeSpan.FromSeconds(10)));

        var deletionApplicationName = $"price-delete-{suffix}";
        var deletionStore = new PostgresDataStore(
            WithApplicationName(connectionString, deletionApplicationName));
        var deletionTask = Task.Run(() => Record.Exception(() =>
            new PartnerItemSalePriceService(deletionStore).Delete(priceId)));

        await WaitUntilSessionWaitsForLock(connectionString, deletionApplicationName);
        allowReactivationCommit.Set();

        Assert.Null(await reactivationTask.WaitAsync(TimeSpan.FromSeconds(10)));
        var deletionError = Assert.IsType<CommercialTermsException>(
            await deletionTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            "PARTNER_ITEM_PRICE_MUST_BE_INACTIVE_BEFORE_DELETE",
            deletionError.ErrorCode);
        Assert.True(setupStore.GetPartnerItemSalePrice(priceId)?.IsActive);
    }

    [Fact]
    public void Existing_inactive_vat_rate_is_preserved_during_unrelated_item_update()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var vatRateId = new VatRateService(store).CreateVatRate(
            $"VAT-INACTIVE-{suffix}",
            CreateUniqueRate(),
            0,
            isActive: true);
        var catalog = new CatalogService(store);
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"VAT-INACTIVE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            defaultSaleVatRateId: vatRateId);
        var vatRate = store.GetVatRate(vatRateId)!;
        new VatRateService(store).UpdateVatRate(
            vatRateId,
            vatRate.Name,
            vatRate.Rate,
            vatRate.SortOrder,
            isActive: false);

        catalog.UpdateItem(
            itemId,
            name: $"Товар переименован {suffix}",
            barcode: $"VAT-INACTIVE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            defaultSaleVatRateId: vatRateId);

        var updated = store.FindItemById(itemId);
        Assert.Equal($"Товар переименован {suffix}", updated?.Name);
        Assert.Equal(vatRateId, updated?.DefaultSaleVatRateId);
    }

    [Fact]
    public void Used_vat_rate_value_cannot_be_changed()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var originalRate = CreateUniqueRate();
        var vatName = $"VAT-USED-{suffix}";
        var vatRateId = new VatRateService(store)
            .CreateVatRate(vatName, originalRate, 0, isActive: true);
        new CatalogService(store).CreateItem(
            name: $"Товар {suffix}",
            barcode: $"VAT-USED-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false,
            defaultSaleVatRateId: vatRateId);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new VatRateService(store).UpdateVatRate(
                vatRateId,
                vatName,
                originalRate - 0.0001m,
                0,
                isActive: true));

        Assert.Contains("Нельзя изменить числовое значение", error.Message);
        Assert.Equal(originalRate, store.GetVatRate(vatRateId)?.Rate);
    }

    [Fact]
    public async Task Assigning_vat_rate_serializes_with_deactivation()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var vatName = $"VAT-DEACTIVATE-{suffix}";
        var barcode = $"VAT-DEACTIVATE-{suffix}";
        var rate = CreateUniqueRate();
        var setupStore = new PostgresDataStore(connectionString);
        var vatRateId = new VatRateService(setupStore)
            .CreateVatRate(vatName, rate, 0, isActive: true);

        await using var blockerConnection = new NpgsqlConnection(connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = blockerConnection.CreateCommand())
        {
            blockerCommand.Transaction = blockerTransaction;
            blockerCommand.CommandText = "LOCK TABLE items IN SHARE MODE;";
            await blockerCommand.ExecuteNonQueryAsync();
        }

        var assignmentApplicationName = $"vat-deactivate-assign-{suffix}";
        var assignmentStore = new PostgresDataStore(
            WithApplicationName(connectionString, assignmentApplicationName));
        var assignmentTask = Task.Run(() => Record.Exception(() =>
            new CatalogService(assignmentStore).CreateItem(
                name: $"Товар {suffix}",
                barcode: barcode,
                gtin: null,
                baseUom: "шт",
                brand: null,
                volume: null,
                shelfLifeMonths: null,
                taraId: null,
                isMarked: false,
                defaultSaleVatRateId: vatRateId)));

        await WaitUntilSessionWaitsForLock(connectionString, assignmentApplicationName);

        var deactivationStore = new PostgresDataStore(
            WithApplicationName(connectionString, $"vat-deactivate-update-{suffix}"));
        var deactivationTask = Task.Run(() => Record.Exception(() =>
            new VatRateService(deactivationStore).UpdateVatRate(
                vatRateId,
                vatName,
                rate,
                0,
                isActive: false)));

        await Task.Delay(250);
        Assert.False(
            deactivationTask.IsCompleted,
            "Деактивация не должна завершиться до атомарного назначения ставки.");

        await blockerTransaction.CommitAsync();

        Assert.Null(await assignmentTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await deactivationTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(setupStore.GetVatRate(vatRateId)?.IsActive);
        Assert.Equal(vatRateId, setupStore.GetItems(barcode).Single().DefaultSaleVatRateId);
    }

    [Fact]
    public async Task Assigning_vat_rate_serializes_with_rate_value_change()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var vatName = $"VAT-CONCURRENCY-{suffix}";
        var barcode = $"VAT-CONCURRENCY-{suffix}";
        var originalRate = CreateUniqueRate();
        var changedRate = originalRate - 0.0001m;
        var setupStore = new PostgresDataStore(connectionString);
        var vatRateId = new VatRateService(setupStore)
            .CreateVatRate(vatName, originalRate, 0, isActive: true);

        await using var blockerConnection = new NpgsqlConnection(connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = blockerConnection.CreateCommand())
        {
            blockerCommand.Transaction = blockerTransaction;
            blockerCommand.CommandText = "LOCK TABLE items IN SHARE MODE;";
            await blockerCommand.ExecuteNonQueryAsync();
        }

        var assignmentApplicationName = $"vat-assign-{suffix}";
        var assignmentStore = new PostgresDataStore(
            WithApplicationName(connectionString, assignmentApplicationName));
        var assignmentTask = Task.Run(() => Record.Exception(() =>
            new CatalogService(assignmentStore).CreateItem(
                name: $"Товар {suffix}",
                barcode: barcode,
                gtin: null,
                baseUom: "шт",
                brand: null,
                volume: null,
                shelfLifeMonths: null,
                taraId: null,
                isMarked: false,
                defaultSaleVatRateId: vatRateId)));

        await WaitUntilSessionWaitsForLock(connectionString, assignmentApplicationName);

        var updateStore = new PostgresDataStore(
            WithApplicationName(connectionString, $"vat-update-{suffix}"));
        var updateTask = Task.Run(() => Record.Exception(() =>
            new VatRateService(updateStore).UpdateVatRate(
                vatRateId,
                vatName,
                changedRate,
                0,
                isActive: true)));

        await Task.Delay(250);
        await blockerTransaction.CommitAsync();

        var assignmentError = await assignmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        var updateError = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(assignmentError);
        Assert.IsType<InvalidOperationException>(updateError);
        Assert.Equal(originalRate, setupStore.GetVatRate(vatRateId)?.Rate);
        Assert.Equal(vatRateId, setupStore.GetItems(barcode).Single().DefaultSaleVatRateId);
    }

    private static async Task WaitUntilSessionWaitsForLock(
        string connectionString,
        string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT EXISTS (
    SELECT 1
    FROM pg_stat_activity
    WHERE application_name = @application_name
      AND wait_event_type = 'Lock'
);
""";
            command.Parameters.AddWithValue("@application_name", applicationName);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync()))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Сессия {applicationName} не перешла в ожидание PostgreSQL lock.");
    }

    private static string WithApplicationName(string connectionString, string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static decimal CreateUniqueRate() =>
        100m + ((uint)Guid.NewGuid().GetHashCode() % 8_000_000) / 10_000m;

    private static string ResolveRequiredPostgresTestConnectionString()
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

        throw new InvalidOperationException(
            "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
    }
}
