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
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", null, "CLIENT");
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
    public void Missing_customer_price_update_wins_over_invalid_references()
    {
        var store = new PostgresDataStore(ResolveRequiredPostgresTestConnectionString());

        var error = Assert.Throws<CommercialTermsException>(
            () => new PartnerItemSalePriceService(store).Update(
                long.MaxValue,
                long.MaxValue,
                long.MaxValue,
                100m,
                isActive: true));

        Assert.Equal("PARTNER_ITEM_SALE_PRICE_NOT_FOUND", error.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Partner_with_customer_price_cannot_become_supplier(bool isActive)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(store);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", null, "CLIENT");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"PARTNER-ROLE-PRICE-{isActive}-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);
        new PartnerItemSalePriceService(store).Create(
            partnerId,
            itemId,
            123.45m,
            isActive);

        var error = Assert.Throws<CommercialTermsException>(() =>
            catalog.UpdatePartner(partnerId, $"Клиент {suffix}", null, "SUPPLIER"));

        Assert.Equal("PARTNER_HAS_CUSTOMER_PRICES", error.ErrorCode);
        Assert.Equal("CLIENT", store.GetPartner(partnerId)?.PartnerRole);
        Assert.True(store.HasPartnerItemSalePricesForPartner(partnerId));
    }

    [Fact]
    public void Partner_without_customer_prices_can_become_supplier()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(store);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", null, "BOTH");

        catalog.UpdatePartner(partnerId, $"Поставщик {suffix}", null, "SUPPLIER");

        var updated = store.GetPartner(partnerId);
        Assert.Equal($"Поставщик {suffix}", updated?.Name);
        Assert.Equal("SUPPLIER", updated?.PartnerRole);
        Assert.False(store.HasPartnerItemSalePricesForPartner(partnerId));
    }

    [Fact]
    public void Customer_price_create_for_supplier_is_rejected_by_canonical_service()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(store);
        var partnerId = catalog.CreatePartner($"Поставщик {suffix}", null, "SUPPLIER");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"SUPPLIER-PRICE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);

        var error = Assert.Throws<CommercialTermsException>(() =>
            new PartnerItemSalePriceService(store).Create(
                partnerId,
                itemId,
                123.45m,
                isActive: true));

        Assert.Equal("PARTNER_IS_SUPPLIER", error.ErrorCode);
        Assert.False(store.HasPartnerItemSalePricesForPartner(partnerId));
    }

    [Fact]
    public void Customer_price_cannot_be_reassigned_to_supplier()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(store);
        var clientId = catalog.CreatePartner($"Клиент {suffix}", null, "CLIENT");
        var supplierId = catalog.CreatePartner($"Поставщик {suffix}", null, "SUPPLIER");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"SUPPLIER-PRICE-MOVE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);
        var service = new PartnerItemSalePriceService(store);
        var priceId = service.Create(clientId, itemId, 123.45m, isActive: false);

        var error = Assert.Throws<CommercialTermsException>(() =>
            service.Update(priceId, supplierId, itemId, 150m, isActive: false));

        Assert.Equal("PARTNER_IS_SUPPLIER", error.ErrorCode);
        Assert.Equal(clientId, store.GetPartnerItemSalePrice(priceId)?.PartnerId);
        Assert.False(store.HasPartnerItemSalePricesForPartner(supplierId));
    }

    [Fact]
    public async Task Supplier_transition_and_customer_price_create_are_serialized()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var setupStore = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(setupStore);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", null, "CLIENT");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"PARTNER-PRICE-RACE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);

        await using var blockerConnection = new NpgsqlConnection(connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = blockerConnection.CreateCommand())
        {
            blockerCommand.Transaction = blockerTransaction;
            blockerCommand.CommandText = "SELECT id FROM partners WHERE id = @id FOR UPDATE;";
            blockerCommand.Parameters.AddWithValue("@id", partnerId);
            await blockerCommand.ExecuteScalarAsync();
        }

        var supplierApplicationName = $"partner-supplier-race-{suffix}";
        var supplierStore = new PostgresDataStore(
            WithApplicationName(connectionString, supplierApplicationName));
        var supplierTask = Task.Run(() => Record.Exception(() =>
            new CatalogService(supplierStore).UpdatePartner(
                partnerId,
                $"Поставщик {suffix}",
                null,
                "SUPPLIER")));

        var priceApplicationName = $"partner-price-race-{suffix}";
        var priceStore = new PostgresDataStore(
            WithApplicationName(connectionString, priceApplicationName));
        var priceTask = Task.Run(() => Record.Exception(() =>
            new PartnerItemSalePriceService(priceStore).Create(
                partnerId,
                itemId,
                123.45m,
                isActive: true)));

        await WaitUntilSessionWaitsForLock(connectionString, supplierApplicationName);
        await WaitUntilSessionWaitsForLock(connectionString, priceApplicationName);
        await blockerTransaction.CommitAsync();

        var supplierError = await supplierTask.WaitAsync(TimeSpan.FromSeconds(10));
        var priceError = await priceTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(supplierError == null, priceError == null);
        if (supplierError != null)
        {
            Assert.Equal(
                "PARTNER_HAS_CUSTOMER_PRICES",
                Assert.IsType<CommercialTermsException>(supplierError).ErrorCode);
        }
        if (priceError != null)
        {
            Assert.Equal(
                "PARTNER_IS_SUPPLIER",
                Assert.IsType<CommercialTermsException>(priceError).ErrorCode);
        }

        var finalRole = setupStore.GetPartner(partnerId)?.PartnerRole;
        var hasPrice = setupStore.HasPartnerItemSalePricesForPartner(partnerId);
        Assert.True(
            (finalRole == "SUPPLIER" && !hasPrice)
            || (finalRole is "CLIENT" or "BOTH" && hasPrice),
            $"Недопустимое итоговое состояние: role={finalRole}, hasPrice={hasPrice}.");
    }

    [Fact]
    public async Task Deleting_customer_price_serializes_with_reactivation()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var setupStore = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(setupStore);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", null, "CLIENT");
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
    public async Task Updating_customer_price_reports_not_found_when_delete_commits_first()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var setupStore = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(setupStore);
        var partnerId = catalog.CreatePartner($"Клиент {suffix}", null, "CLIENT");
        var itemId = catalog.CreateItem(
            name: $"Товар {suffix}",
            barcode: $"PRICE-DELETE-FIRST-{suffix}",
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

        using var deleteWritten = new ManualResetEventSlim();
        using var allowDeleteCommit = new ManualResetEventSlim();
        var deletionStore = new PostgresDataStore(
            WithApplicationName(connectionString, $"price-delete-first-{suffix}"));
        var deletionTask = Task.Run(() => Record.Exception(() =>
            deletionStore.ExecuteInTransaction(scopedStore =>
            {
                new PartnerItemSalePriceService(scopedStore).Delete(priceId);
                deleteWritten.Set();
                Assert.True(allowDeleteCommit.Wait(TimeSpan.FromSeconds(10)));
            })));

        Assert.True(deleteWritten.Wait(TimeSpan.FromSeconds(10)));

        var updateApplicationName = $"price-update-after-delete-{suffix}";
        var updateStore = new PostgresDataStore(
            WithApplicationName(connectionString, updateApplicationName));
        var updateTask = Task.Run(() => Record.Exception(() =>
            new PartnerItemSalePriceService(updateStore).Update(
                priceId,
                partnerId,
                itemId,
                150m,
                isActive: true)));

        await WaitUntilSessionWaitsForLock(connectionString, updateApplicationName);
        allowDeleteCommit.Set();

        Assert.Null(await deletionTask.WaitAsync(TimeSpan.FromSeconds(10)));
        var updateError = Assert.IsType<CommercialTermsException>(
            await updateTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("PARTNER_ITEM_SALE_PRICE_NOT_FOUND", updateError.ErrorCode);
        Assert.Null(setupStore.GetPartnerItemSalePrice(priceId));
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
        var blockerPid = Convert.ToInt32(
            await new NpgsqlCommand("SELECT pg_backend_pid();", blockerConnection).ExecuteScalarAsync());
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = blockerConnection.CreateCommand())
        {
            blockerCommand.Transaction = blockerTransaction;
            blockerCommand.CommandText = "LOCK TABLE items IN SHARE MODE;";
            await blockerCommand.ExecuteNonQueryAsync();
        }

        var interferenceApplicationName = $"vat-interference-{suffix}";
        var interferenceTask = Task.Run(() => Record.Exception(() =>
        {
            using var connection = new NpgsqlConnection(
                WithApplicationName(connectionString, interferenceApplicationName));
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SET LOCAL lock_timeout = '200ms'; LOCK TABLE items IN ACCESS EXCLUSIVE MODE;";
            command.ExecuteNonQuery();
            transaction.Commit();
        }));
        await WaitUntilSessionWaitsForSpecificLock(
            connectionString,
            interferenceApplicationName,
            blockerPid,
            "LOCK TABLE items IN ACCESS EXCLUSIVE MODE");

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

        var assignmentPid = await WaitUntilSessionWaitsForSpecificLock(
            connectionString,
            assignmentApplicationName,
            blockerPid,
            "INSERT INTO items");
        var interferenceError = Assert.IsType<PostgresException>(
            await interferenceTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, interferenceError.SqlState);

        var updateApplicationName = $"vat-update-{suffix}";
        var updateStore = new PostgresDataStore(
            WithApplicationName(connectionString, updateApplicationName));
        var updateTask = Task.Run(() => Record.Exception(() =>
            new VatRateService(updateStore).UpdateVatRate(
                vatRateId,
                vatName,
                changedRate,
                0,
                isActive: true)));

        await WaitUntilSessionWaitsForSpecificLock(
            connectionString,
            updateApplicationName,
            assignmentPid,
            "FROM vat_rates");
        Assert.False(updateTask.IsCompleted);
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

    private static async Task<int> WaitUntilSessionWaitsForSpecificLock(
        string connectionString,
        string applicationName,
        int blockerPid,
        string queryFragment)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT pid
FROM pg_stat_activity
WHERE application_name = @application_name
  AND wait_event_type = 'Lock'
  AND @blocker_pid = ANY(pg_blocking_pids(pid))
  AND STRPOS(UPPER(query), UPPER(@query_fragment)) > 0
ORDER BY pid
LIMIT 1;
""";
            command.Parameters.AddWithValue("@application_name", applicationName);
            command.Parameters.AddWithValue("@blocker_pid", blockerPid);
            command.Parameters.AddWithValue("@query_fragment", queryFragment);
            var result = await command.ExecuteScalarAsync();
            if (result != null && result is not DBNull)
            {
                return Convert.ToInt32(result);
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Сессия {applicationName} не ждала PostgreSQL lock от PID {blockerPid} " +
            $"на SQL-фрагменте '{queryFragment}'.");
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
