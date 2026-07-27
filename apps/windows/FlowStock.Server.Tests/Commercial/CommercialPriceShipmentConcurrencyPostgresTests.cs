using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialPriceShipmentConcurrencyPostgresTests
{
    [Fact]
    public async Task Outbound_fixture_closes_before_concurrency_is_applied()
    {
        await using var fixture = new PriceShipmentFixture(
            ResolveRequiredPostgresTestConnectionString());
        var scenario = fixture.CreateScenario(originalPrice: 100m);

        var result = new DocumentService(fixture.Store)
            .TryCloseDoc(scenario.DocId, allowNegative: true);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task Close_commits_first_then_price_update_is_rejected_and_sales_keep_original_price()
    {
        await using var fixture = new PriceShipmentFixture(
            ResolveRequiredPostgresTestConnectionString());
        var scenario = fixture.CreateScenario(originalPrice: 100m);

        using var closePrepared = new ManualResetEventSlim();
        using var allowCloseCommit = new ManualResetEventSlim();
        CloseDocResult? closeResult = null;
        var closeStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, $"{fixture.Prefix}-close-first"));
        var closeTask = Task.Run(() => Record.Exception(() =>
            closeStore.ExecuteInTransaction(scopedStore =>
            {
                closeResult = new DocumentService(scopedStore)
                    .TryCloseDoc(scenario.DocId, allowNegative: true);
                Assert.True(
                    closeResult.Success,
                    string.Join(Environment.NewLine, closeResult.Errors));
                closePrepared.Set();
                Assert.True(allowCloseCommit.Wait(TimeSpan.FromSeconds(10)));
            })));

        Assert.True(closePrepared.Wait(TimeSpan.FromSeconds(10)));

        var updateApplicationName = $"{fixture.Prefix}-update-after-close";
        var updateStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, updateApplicationName));
        var updateTask = Task.Run(() => Record.Exception(() =>
            UpdatePrice(updateStore, scenario, 150m)));

        await WaitUntilSessionWaitsForLock(
            fixture.ConnectionString,
            updateApplicationName);
        allowCloseCommit.Set();

        Assert.Null(await closeTask.WaitAsync(TimeSpan.FromSeconds(10)));
        var updateError = Assert.IsType<CommercialTermsException>(
            await updateTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            "ORDER_LINE_PRICE_LOCKED_BY_SHIPMENT",
            updateError.ErrorCode);
        Assert.Equal(
            100m,
            fixture.Store.GetOrderLines(scenario.OrderId)
                .Single(line => line.Id == scenario.OrderLineId)
                .UnitPriceGross);
        Assert.Equal(100m, fixture.GetSalesGross(scenario.ItemId));
    }

    [Fact]
    public async Task Price_update_commits_first_then_close_uses_updated_snapshot()
    {
        await using var fixture = new PriceShipmentFixture(
            ResolveRequiredPostgresTestConnectionString());
        var scenario = fixture.CreateScenario(originalPrice: 100m);

        using var updatePrepared = new ManualResetEventSlim();
        using var allowUpdateCommit = new ManualResetEventSlim();
        var updateStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, $"{fixture.Prefix}-update-first"));
        var updateTask = Task.Run(() => Record.Exception(() =>
            updateStore.ExecuteInTransaction(scopedStore =>
            {
                UpdatePrice(scopedStore, scenario, 150m);
                updatePrepared.Set();
                Assert.True(allowUpdateCommit.Wait(TimeSpan.FromSeconds(10)));
            })));

        Assert.True(updatePrepared.Wait(TimeSpan.FromSeconds(10)));

        var closeApplicationName = $"{fixture.Prefix}-close-after-update";
        var closeStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, closeApplicationName));
        CloseDocResult? closeResult = null;
        var closeTask = Task.Run(() => Record.Exception(() =>
        {
            closeResult = new DocumentService(closeStore)
                .TryCloseDoc(scenario.DocId, allowNegative: true);
        }));

        await WaitUntilSessionWaitsForLock(
            fixture.ConnectionString,
            closeApplicationName);
        allowUpdateCommit.Set();

        Assert.Null(await updateTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await closeTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(closeResult);
        Assert.True(
            closeResult.Success,
            string.Join(Environment.NewLine, closeResult.Errors));
        Assert.Equal(
            150m,
            fixture.Store.GetOrderLines(scenario.OrderId)
                .Single(line => line.Id == scenario.OrderLineId)
                .UnitPriceGross);
        Assert.Equal(150m, fixture.GetSalesGross(scenario.ItemId));
    }

    [Fact]
    public async Task Headerless_line_linked_outbound_is_rejected_during_concurrent_price_update()
    {
        await using var fixture = new PriceShipmentFixture(
            ResolveRequiredPostgresTestConnectionString());
        var scenario = fixture.CreateScenario(
            originalPrice: 100m,
            includeHeaderOrderId: false);

        using var start = new ManualResetEventSlim();
        var updateStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, $"{fixture.Prefix}-headerless-update"));
        var updateTask = Task.Run(() => Record.Exception(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            UpdatePrice(updateStore, scenario, 150m);
        }));

        var closeApplicationName = $"{fixture.Prefix}-headerless-close";
        var closeStore = new PostgresDataStore(
            WithApplicationName(fixture.ConnectionString, closeApplicationName));
        CloseDocResult? closeResult = null;
        var closeTask = Task.Run(() => Record.Exception(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            closeResult = new DocumentService(closeStore)
                .TryCloseDoc(scenario.DocId, allowNegative: true);
        }));

        start.Set();
        Assert.Null(await closeTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await updateTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(closeResult);
        Assert.False(closeResult.Success);
        Assert.Contains(
            "OUTBOUND_ORDER_HEADER_REQUIRED_FOR_LINE_LINK",
            closeResult.Errors);
        Assert.Equal(DocStatus.Draft, fixture.Store.GetDoc(scenario.DocId)?.Status);
        Assert.False(fixture.Store.HasCommercialShipmentForOrderLine(scenario.OrderLineId));
        Assert.Equal(
            150m,
            fixture.Store.GetOrderLines(scenario.OrderId)
                .Single(line => line.Id == scenario.OrderLineId)
                .UnitPriceGross);
        Assert.Equal(0m, fixture.GetSalesGross(scenario.ItemId));
    }

    private static void UpdatePrice(
        IDataStore store,
        PriceShipmentScenario scenario,
        decimal price)
    {
        new OrderService(store).UpdateOrder(
            scenario.OrderId,
            scenario.OrderRef,
            scenario.PartnerId,
            dueDate: null,
            comment: null,
            lines:
            [
                new OrderLineView
                {
                    Id = scenario.OrderLineId,
                    ItemId = scenario.ItemId,
                    QtyOrdered = 10,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                    ChangeUnitPriceGross = true,
                    UnitPriceGross = price
                }
            ],
            type: OrderType.Customer,
            bindReservedStockForCustomer: false);
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

    private static string WithApplicationName(
        string connectionString,
        string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = false
        };
        return builder.ConnectionString;
    }

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

    private sealed class PriceShipmentFixture : IAsyncDisposable
    {
        private readonly List<long> _partnerIds = [];
        private readonly List<long> _itemIds = [];
        private readonly List<long> _orderIds = [];
        private readonly List<long> _docIds = [];
        private readonly List<long> _locationIds = [];

        public PriceShipmentFixture(string connectionString)
        {
            ConnectionString = connectionString;
            Prefix = $"CPRICE-{Guid.NewGuid():N}";
            Store = new PostgresDataStore(connectionString);
        }

        public string ConnectionString { get; }
        public string Prefix { get; }
        public PostgresDataStore Store { get; }

        public PriceShipmentScenario CreateScenario(
            decimal originalPrice,
            bool includeHeaderOrderId = true)
        {
            var catalog = new CatalogService(Store);
            var partnerId = catalog.CreatePartner(
                $"Клиент {Prefix}",
                $"{Prefix}-PARTNER");
            _partnerIds.Add(partnerId);
            var itemId = catalog.CreateItem(
                name: $"Товар {Prefix}",
                barcode: $"{Prefix}-ITEM",
                gtin: $"{Prefix}-GTIN",
                baseUom: "шт",
                brand: Prefix,
                volume: "1 л",
                shelfLifeMonths: null,
                taraId: null,
                isMarked: false);
            _itemIds.Add(itemId);
            var orderRef = $"{Prefix}-ORDER";
            var orderId = Store.AddOrder(new Order
            {
                OrderRef = orderRef,
                Type = OrderType.Customer,
                PartnerId = partnerId,
                Status = OrderStatus.InProgress,
                CreatedAt = DateTime.Now
            });
            _orderIds.Add(orderId);
            var orderLineId = Store.AddOrderLine(new OrderLine
            {
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = 10,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                UnitPriceGross = originalPrice,
                VatRate = 22m
            });
            var locationId = Store.AddLocation(new Location
            {
                Code = $"{Prefix}-LOC",
                Name = $"Склад {Prefix}",
                AutoHuDistributionEnabled = true
            });
            _locationIds.Add(locationId);
            var stockDocId = Store.AddDoc(new Doc
            {
                DocRef = $"{Prefix}-STOCK",
                Type = DocType.InventoryCorrection,
                Status = DocStatus.Closed,
                CreatedAt = DateTime.Now.AddMinutes(-2),
                ClosedAt = DateTime.Now.AddMinutes(-1)
            });
            _docIds.Add(stockDocId);
            Store.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = DateTime.Now.AddMinutes(-1),
                DocId = stockDocId,
                ItemId = itemId,
                LocationId = locationId,
                QtyDelta = 10
            });
            var docId = Store.AddDoc(new Doc
            {
                DocRef = $"{Prefix}-OUT",
                Type = DocType.Outbound,
                Status = DocStatus.Draft,
                CreatedAt = DateTime.Now,
                PartnerId = partnerId,
                OrderId = includeHeaderOrderId ? orderId : null,
                OrderRef = includeHeaderOrderId ? orderRef : null
            });
            _docIds.Add(docId);
            Store.AddDocLine(new DocLine
            {
                DocId = docId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                Qty = 1,
                QtyInput = 1,
                UomCode = "BASE",
                FromLocationId = locationId,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });

            return new PriceShipmentScenario(
                partnerId,
                itemId,
                orderId,
                orderLineId,
                docId,
                orderRef);
        }

        public decimal GetSalesGross(long itemId)
        {
            var result = Store.GetCommercialStatistics(new CommercialStatisticsQuery(
                CommercialStatisticsMode.Sales,
                CommercialStatisticsGroupBy.Item,
                DateTime.Today.AddDays(-1),
                DateTime.Today.AddDays(2),
                DetailMonth: null,
                PartnerId: null,
                ItemId: itemId,
                Gtin: null,
                Brand: null,
                Volume: null,
                Statuses: Array.Empty<OrderStatus>(),
                Limit: 100,
                Offset: 0,
                Sort: "gross_desc"));
            return result.Summary.Gross;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Execute(
                connection,
                transaction,
                """
DELETE FROM business_notifications
WHERE (entity_type = 'DOC' AND entity_id = ANY(@doc_ids))
   OR (entity_type = 'ORDER' AND entity_id = ANY(@order_ids));
""");
            await Execute(
                connection,
                transaction,
                "DELETE FROM ledger WHERE doc_id = ANY(@doc_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM doc_lines WHERE doc_id = ANY(@doc_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM docs WHERE id = ANY(@doc_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM order_receipt_plan_lines WHERE order_id = ANY(@order_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM order_lines WHERE order_id = ANY(@order_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM orders WHERE id = ANY(@order_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM partner_item_sale_prices WHERE partner_id = ANY(@partner_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM items WHERE id = ANY(@item_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM locations WHERE id = ANY(@location_ids);");
            await Execute(
                connection,
                transaction,
                "DELETE FROM partners WHERE id = ANY(@partner_ids);");
            await transaction.CommitAsync();
        }

        private async Task Execute(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@doc_ids", _docIds.ToArray());
            command.Parameters.AddWithValue("@order_ids", _orderIds.ToArray());
            command.Parameters.AddWithValue("@partner_ids", _partnerIds.ToArray());
            command.Parameters.AddWithValue("@item_ids", _itemIds.ToArray());
            command.Parameters.AddWithValue("@location_ids", _locationIds.ToArray());
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed record PriceShipmentScenario(
        long PartnerId,
        long ItemId,
        long OrderId,
        long OrderLineId,
        long DocId,
        string OrderRef);
}
