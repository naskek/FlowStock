using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialStatisticsPostgresTests
{
    [Fact]
    public async Task Grouping_pagination_detail_month_sorting_and_read_only_behavior_are_real_sql()
    {
        await using var fixture = new StatisticsFixture(
            ResolveRequiredPostgresTestConnectionString());
        var partnerA = fixture.AddPartner("GROUP-A");
        var partnerB = fixture.AddPartner("GROUP-B");
        var brand1 = $"{fixture.Prefix}-BRAND-1";
        var brand2 = $"{fixture.Prefix}-BRAND-2";
        var volume1 = $"{fixture.Prefix}-VOLUME-1";
        var volume2 = $"{fixture.Prefix}-VOLUME-2";
        var itemA = fixture.AddItem("GROUP-A", "G1", brand1, volume1);
        var itemB = fixture.AddItem("GROUP-B", "G2", brand1, volume2);
        var itemC = fixture.AddItem("GROUP-C", "G3", brand2, volume2);

        var januaryA = fixture.AddOrder(
            "GROUP-JAN-A",
            OrderType.Customer,
            partnerA,
            OrderStatus.Accepted,
            new DateTime(2043, 1, 10));
        fixture.AddOrderLine(januaryA, itemA, 1, 120m, 0m);
        var januaryB = fixture.AddOrder(
            "GROUP-JAN-B",
            OrderType.Customer,
            partnerB,
            OrderStatus.Accepted,
            new DateTime(2043, 1, 11));
        fixture.AddOrderLine(januaryB, itemB, 2, 50m, 0m);
        var februaryA = fixture.AddOrder(
            "GROUP-FEB-A",
            OrderType.Customer,
            partnerA,
            OrderStatus.Accepted,
            new DateTime(2043, 2, 10));
        fixture.AddOrderLine(februaryA, itemC, 3, 10m, 0m);

        var expectedGroupCounts = new Dictionary<CommercialStatisticsGroupBy, int>
        {
            [CommercialStatisticsGroupBy.Partner] = 2,
            [CommercialStatisticsGroupBy.Item] = 3,
            [CommercialStatisticsGroupBy.Gtin] = 3,
            [CommercialStatisticsGroupBy.Brand] = 2,
            [CommercialStatisticsGroupBy.Volume] = 2
        };
        foreach (var (groupBy, expectedCount) in expectedGroupCounts)
        {
            var grouped = fixture.Store.GetCommercialStatistics(Query(
                CommercialStatisticsMode.Orders,
                groupBy,
                from: new DateTime(2043, 1, 1),
                toExclusive: new DateTime(2043, 3, 1)));
            Assert.Equal(expectedCount, grouped.TotalGroupCount);
            Assert.Equal(expectedCount, grouped.Groups.Count);
            var actual = grouped.Groups.ToDictionary(
                row => row.Key ?? string.Empty,
                row => (row.Amounts.Quantity, row.Amounts.Gross));
            var expected = groupBy switch
            {
                CommercialStatisticsGroupBy.Partner =>
                    new Dictionary<string, (decimal Quantity, decimal Gross)>
                    {
                        [partnerA.ToString()] = (4m, 150m),
                        [partnerB.ToString()] = (2m, 100m)
                    },
                CommercialStatisticsGroupBy.Item =>
                    new Dictionary<string, (decimal Quantity, decimal Gross)>
                    {
                        [itemA.ToString()] = (1m, 120m),
                        [itemB.ToString()] = (2m, 100m),
                        [itemC.ToString()] = (3m, 30m)
                    },
                CommercialStatisticsGroupBy.Gtin =>
                    new Dictionary<string, (decimal Quantity, decimal Gross)>
                    {
                        [$"{fixture.Prefix}-G1"] = (1m, 120m),
                        [$"{fixture.Prefix}-G2"] = (2m, 100m),
                        [$"{fixture.Prefix}-G3"] = (3m, 30m)
                    },
                CommercialStatisticsGroupBy.Brand =>
                    new Dictionary<string, (decimal Quantity, decimal Gross)>
                    {
                        [brand1] = (3m, 220m),
                        [brand2] = (3m, 30m)
                    },
                CommercialStatisticsGroupBy.Volume =>
                    new Dictionary<string, (decimal Quantity, decimal Gross)>
                    {
                        [volume1] = (1m, 120m),
                        [volume2] = (5m, 130m)
                    },
                _ => throw new ArgumentOutOfRangeException()
            };
            Assert.Equal(expected, actual);
        }

        var firstPage = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Item,
            from: new DateTime(2043, 1, 1),
            toExclusive: new DateTime(2043, 3, 1),
            limit: 1,
            offset: 0));
        var secondPage = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Item,
            from: new DateTime(2043, 1, 1),
            toExclusive: new DateTime(2043, 3, 1),
            limit: 1,
            offset: 1));
        Assert.Equal(3, firstPage.TotalGroupCount);
        Assert.Equal(itemA.ToString(), Assert.Single(firstPage.Groups).Key);
        Assert.Equal(itemB.ToString(), Assert.Single(secondPage.Groups).Key);

        var detail = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Item,
            from: new DateTime(2043, 1, 1),
            toExclusive: new DateTime(2043, 3, 1),
            detailMonth: new DateTime(2043, 2, 1)));
        Assert.Equal(2, detail.Monthly.Count);
        Assert.Equal(["2043-01", "2043-02"], detail.Monthly.Select(row => row.Month));
        Assert.Equal(220m, detail.Monthly[0].Amounts.Gross);
        Assert.Equal(30m, detail.Monthly[1].Amounts.Gross);
        Assert.Equal(itemC.ToString(), Assert.Single(detail.Groups).Key);
        Assert.Equal(250m, detail.Summary.Gross);

        var quantitySorted = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Partner,
            from: new DateTime(2043, 1, 1),
            toExclusive: new DateTime(2043, 3, 1),
            sort: "quantity_desc"));
        Assert.Equal(partnerA.ToString(), quantitySorted.Groups[0].Key);
        Assert.Equal(4m, quantitySorted.Groups[0].Amounts.Quantity);

        var before = SnapshotOrder(fixture.Store.GetOrder(januaryA));
        var beforeLines = SnapshotLines(fixture.Store.GetOrderLines(januaryA));
        var beforeLedgerCount = await fixture.CountLedgerRowsForCreatedItems();
        _ = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Brand,
            from: new DateTime(2043, 1, 1),
            toExclusive: new DateTime(2043, 3, 1)));
        Assert.Equal(before, SnapshotOrder(fixture.Store.GetOrder(januaryA)));
        Assert.Equal(beforeLines, SnapshotLines(fixture.Store.GetOrderLines(januaryA)));
        Assert.Equal(beforeLedgerCount, await fixture.CountLedgerRowsForCreatedItems());
    }

    private static object? SnapshotOrder(Order? order) =>
        order == null
            ? null
            : new
            {
                order.Id,
                order.OrderRef,
                order.Type,
                order.PartnerId,
                order.Status,
                order.Comment,
                order.CreatedAt
            };

    private static object[] SnapshotLines(IEnumerable<OrderLine> lines) =>
        lines.Select(line => (object)new
        {
            line.Id,
            line.OrderId,
            line.ItemId,
            line.QtyOrdered,
            line.UnitPriceGross,
            line.VatRate,
            line.CancelledAt
        }).ToArray();

    [Fact]
    public async Task Sales_use_closed_docs_partner_active_facts_and_report_data_quality()
    {
        await using var fixture = new StatisticsFixture(
            ResolveRequiredPostgresTestConnectionString());
        var orderPartner = fixture.AddPartner("ORDER");
        var shippingPartner = fixture.AddPartner("SHIPPING");
        var mainItem = fixture.AddItem(
            "SALES",
            "GTIN-SALES",
            fixture.Prefix,
            "750 мл",
            defaultSalePriceGross: 999m);
        var mismatchItem = fixture.AddItem(
            "MISMATCH",
            "GTIN-MISMATCH",
            fixture.Prefix,
            "750 мл",
            defaultSalePriceGross: 888m);
        fixture.Store.AddPartnerItemSalePrice(new PartnerItemSalePrice
        {
            PartnerId = shippingPartner,
            ItemId = mainItem,
            UnitPriceGross = 777m,
            IsActive = true
        });
        var order = fixture.AddOrder(
            "SALES",
            OrderType.Customer,
            orderPartner,
            OrderStatus.Cancelled,
            new DateTime(2041, 12, 1));
        var goodLine = fixture.AddOrderLine(order, mainItem, 20, 100m, 22m);
        var zeroVatLine = fixture.AddOrderLine(order, mainItem, 10, 50m, 0m);
        var legacyLine = fixture.AddOrderLine(order, mainItem, 10, null, null);
        var mismatchLine = fixture.AddOrderLine(order, mainItem, 10, 100m, 22m);

        var firstDoc = fixture.AddDoc(
            "SALES-1",
            shippingPartner,
            order,
            DocStatus.Closed,
            new DateTime(2042, 4, 5, 9, 0, 0));
        fixture.AddDocLine(firstDoc, goodLine, mainItem, 1.25);

        var secondDoc = fixture.AddDoc(
            "SALES-2",
            shippingPartner,
            order,
            DocStatus.Closed,
            new DateTime(2042, 4, 6, 9, 0, 0));
        fixture.AddDocLine(secondDoc, goodLine, mainItem, 0.75);

        var thirdDoc = fixture.AddDoc(
            "SALES-3",
            shippingPartner,
            order,
            DocStatus.Closed,
            new DateTime(2042, 4, 7, 9, 0, 0));
        fixture.AddDocLine(thirdDoc, zeroVatLine, mainItem, 2);

        var mixedDoc = fixture.AddDoc(
            "SALES-4",
            shippingPartner,
            order,
            DocStatus.Closed,
            new DateTime(2042, 4, 8, 9, 0, 0));
        var superseded = fixture.AddDocLine(mixedDoc, goodLine, mainItem, 1);
        fixture.AddDocLine(mixedDoc, goodLine, mainItem, 2, replacesLineId: superseded);
        fixture.AddDocLine(mixedDoc, null, mainItem, 3);
        fixture.AddDocLine(mixedDoc, mismatchLine, mismatchItem, 4);
        fixture.AddDocLine(mixedDoc, legacyLine, mainItem, 5);
        fixture.AddDocLine(mixedDoc, goodLine, mainItem, -1);

        var draftDoc = fixture.AddDoc(
            "SALES-DRAFT",
            shippingPartner,
            order,
            DocStatus.Draft,
            new DateTime(2042, 4, 9, 9, 0, 0));
        fixture.AddDocLine(draftDoc, goodLine, mainItem, 9);
        await fixture.CancelOrderLine(goodLine);

        var result = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Sales,
            CommercialStatisticsGroupBy.Partner,
            from: new DateTime(2042, 4, 1),
            toExclusive: new DateTime(2042, 5, 1),
            brand: fixture.Prefix));

        Assert.Equal(1, result.Summary.OrderCount);
        Assert.Equal(4, result.Summary.DocumentCount);
        Assert.Equal(7, result.Summary.FactCount);
        Assert.Equal(18m, result.Summary.Quantity);
        Assert.Equal(6m, result.Summary.KnownFinancialQuantity);
        Assert.Equal(500m, result.Summary.Gross);
        Assert.Equal(427.87m, result.Summary.Net);
        Assert.Equal(72.13m, result.Summary.Vat);
        var group = Assert.Single(result.Groups);
        Assert.Equal(shippingPartner.ToString(), group.Key);
        Assert.Contains("Клиент SHIPPING", group.Label);

        Assert.Equal(1, result.DataQuality.MissingPriceFactCount);
        Assert.Equal(5m, result.DataQuality.MissingPriceQuantity);
        Assert.Equal(1, result.DataQuality.MissingVatFactCount);
        Assert.Equal(5m, result.DataQuality.MissingVatQuantity);
        Assert.Equal(3, result.DataQuality.FinanciallyIncompleteFactCount);
        Assert.Equal(12m, result.DataQuality.FinanciallyIncompleteQuantity);
        Assert.Equal(1, result.DataQuality.UnlinkedSalesFactCount);
        Assert.Equal(3m, result.DataQuality.UnlinkedSalesQuantity);
        Assert.Equal(1, result.DataQuality.ItemMismatchSalesFactCount);
        Assert.Equal(4m, result.DataQuality.ItemMismatchSalesQuantity);
        Assert.False(result.DataQuality.IsFinanciallyComplete);
    }

    [Fact]
    public async Task Financial_formulas_use_snapshots_and_round_each_fact_before_sum()
    {
        await using var fixture = new StatisticsFixture(
            ResolveRequiredPostgresTestConnectionString());
        var partner = fixture.AddPartner("FORMULA");
        var item = fixture.AddItem(
            "FORMULA",
            "GTIN-FORMULA",
            fixture.Prefix,
            "250 мл",
            defaultSalePriceGross: 999m);
        fixture.Store.AddPartnerItemSalePrice(new PartnerItemSalePrice
        {
            PartnerId = partner,
            ItemId = item,
            UnitPriceGross = 777m,
            IsActive = true
        });
        var order = fixture.AddOrder(
            "FORMULA",
            OrderType.Customer,
            partner,
            OrderStatus.Accepted,
            new DateTime(2042, 3, 10));

        fixture.AddOrderLine(order, item, 1, 122m, 22m);
        fixture.AddOrderLine(order, item, 1, 10.005m, 0m);
        fixture.AddOrderLine(order, item, 2, 0m, 22m);
        fixture.AddOrderLine(order, item, 1.25, 10m, 22m);
        fixture.AddOrderLine(order, item, 1, 0.025m, 0m);
        fixture.AddOrderLine(order, item, 1, 0.025m, 0m);

        var result = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Item,
            from: new DateTime(2042, 3, 1),
            toExclusive: new DateTime(2042, 4, 1),
            brand: fixture.Prefix));

        Assert.Equal(7.25m, result.Summary.Quantity);
        Assert.Equal(7.25m, result.Summary.KnownFinancialQuantity);
        Assert.Equal(144.57m, result.Summary.Gross);
        Assert.Equal(120.32m, result.Summary.Net);
        Assert.Equal(24.25m, result.Summary.Vat);
        Assert.Equal(
            result.Summary.Gross,
            result.Summary.Net + result.Summary.Vat);
    }

    [Fact]
    public async Task Orders_use_created_date_order_partner_and_only_active_customer_lines()
    {
        await using var fixture = new StatisticsFixture(
            ResolveRequiredPostgresTestConnectionString());
        var partnerA = fixture.AddPartner("A");
        var partnerB = fixture.AddPartner("B");
        var item = fixture.AddItem("Main", "GTIN-A", fixture.Prefix, "500 мл");

        var januaryOrder = fixture.AddOrder(
            "JAN",
            OrderType.Customer,
            partnerA,
            OrderStatus.Accepted,
            new DateTime(2042, 1, 10, 12, 0, 0));
        fixture.AddOrderLine(januaryOrder, item, 2, 122m, 22m);

        var februaryOrder = fixture.AddOrder(
            "FEB",
            OrderType.Customer,
            partnerB,
            OrderStatus.Accepted,
            new DateTime(2042, 2, 1, 0, 0, 0));
        fixture.AddOrderLine(februaryOrder, item, 5, 100m, 22m);

        var cancelledLineOrder = fixture.AddOrder(
            "CANCELLED-LINE",
            OrderType.Customer,
            partnerB,
            OrderStatus.Accepted,
            new DateTime(2042, 1, 15, 0, 0, 0));
        var cancelledLineId = fixture.AddOrderLine(
            cancelledLineOrder,
            item,
            7,
            100m,
            22m);
        await fixture.CancelOrderLine(cancelledLineId);

        var internalOrder = fixture.AddOrder(
            "INTERNAL",
            OrderType.Internal,
            null,
            OrderStatus.Accepted,
            new DateTime(2042, 1, 20, 0, 0, 0));
        fixture.AddOrderLine(internalOrder, item, 11, null, null);

        var result = fixture.Store.GetCommercialStatistics(Query(
            CommercialStatisticsMode.Orders,
            CommercialStatisticsGroupBy.Partner,
            from: new DateTime(2042, 1, 1),
            toExclusive: new DateTime(2042, 2, 1),
            brand: fixture.Prefix));

        Assert.Equal(
            new CommercialStatisticsAmounts(
                OrderCount: 1,
                DocumentCount: 0,
                FactCount: 1,
                Quantity: 2m,
                KnownFinancialQuantity: 2m,
                Gross: 244m,
                Net: 200m,
                Vat: 44m),
            result.Summary);
        var group = Assert.Single(result.Groups);
        Assert.Equal(partnerA.ToString(), group.Key);
        Assert.Contains("Клиент A", group.Label);
    }

    private static CommercialStatisticsQuery Query(
        CommercialStatisticsMode mode,
        CommercialStatisticsGroupBy groupBy,
        DateTime from,
        DateTime toExclusive,
        DateTime? detailMonth = null,
        string? brand = null,
        int limit = 100,
        int offset = 0,
        string sort = "gross_desc") =>
        new(
            mode,
            groupBy,
            from,
            toExclusive,
            detailMonth,
            PartnerId: null,
            ItemId: null,
            Gtin: null,
            Brand: brand,
            Volume: null,
            Statuses: mode == CommercialStatisticsMode.Orders
                ? [OrderStatus.Draft, OrderStatus.Accepted, OrderStatus.InProgress, OrderStatus.Shipped]
                : Array.Empty<OrderStatus>(),
            limit,
            offset,
            sort);

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

    private sealed class StatisticsFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly List<long> _partnerIds = [];
        private readonly List<long> _itemIds = [];
        private readonly List<long> _orderIds = [];
        private readonly List<long> _docIds = [];

        public StatisticsFixture(string connectionString)
        {
            _connectionString = connectionString;
            Prefix = $"CSTAT-{Guid.NewGuid():N}";
            Store = new PostgresDataStore(connectionString);
        }

        public string Prefix { get; }
        public PostgresDataStore Store { get; }

        public long AddPartner(string label)
        {
            var id = Store.AddPartner(new Partner
            {
                Name = $"Клиент {label} {Prefix}",
                Code = $"{Prefix}-{label}",
                CreatedAt = new DateTime(2041, 1, 1)
            });
            _partnerIds.Add(id);
            return id;
        }

        public long AddItem(
            string label,
            string gtin,
            string brand,
            string volume,
            decimal? defaultSalePriceGross = null)
        {
            var id = Store.AddItem(new Item
            {
                Name = $"Товар {label} {Prefix}",
                Barcode = $"{Prefix}-{label}",
                Gtin = $"{Prefix}-{gtin}",
                BaseUom = "шт",
                Brand = brand,
                Volume = volume,
                DefaultSalePriceGross = defaultSalePriceGross
            });
            _itemIds.Add(id);
            return id;
        }

        public long AddOrder(
            string label,
            OrderType type,
            long? partnerId,
            OrderStatus status,
            DateTime createdAt)
        {
            var id = Store.AddOrder(new Order
            {
                OrderRef = $"{Prefix}-{label}",
                Type = type,
                PartnerId = partnerId,
                Status = status,
                CreatedAt = createdAt
            });
            _orderIds.Add(id);
            return id;
        }

        public long AddOrderLine(
            long orderId,
            long itemId,
            double quantity,
            decimal? unitPriceGross,
            decimal? vatRate) =>
            Store.AddOrderLine(new OrderLine
            {
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = quantity,
                UnitPriceGross = unitPriceGross,
                VatRate = vatRate,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });

        public long AddDoc(
            string label,
            long partnerId,
            long orderId,
            DocStatus status,
            DateTime closedAt)
        {
            var id = Store.AddDoc(new Doc
            {
                DocRef = $"{Prefix}-{label}",
                Type = DocType.Outbound,
                Status = status,
                CreatedAt = closedAt.AddHours(-1),
                ClosedAt = closedAt,
                PartnerId = partnerId,
                OrderId = orderId,
                OrderRef = $"{Prefix}-{label}"
            });
            _docIds.Add(id);
            return id;
        }

        public long AddDocLine(
            long docId,
            long? orderLineId,
            long itemId,
            double quantity,
            long? replacesLineId = null) =>
            Store.AddDocLine(new DocLine
            {
                DocId = docId,
                ReplacesLineId = replacesLineId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                Qty = quantity,
                QtyInput = quantity,
                UomCode = "BASE",
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });

        public async Task CancelOrderLine(long orderLineId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
UPDATE order_lines
SET cancelled_at = '2042-01-16T00:00:00',
    cancel_reason = 'statistics test'
WHERE id = @id;
""";
            command.Parameters.AddWithValue("@id", orderLineId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task<int> CountLedgerRowsForCreatedItems()
        {
            if (_itemIds.Count == 0)
            {
                return 0;
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ledger WHERE item_id = ANY(@ids);";
            command.Parameters.AddWithValue("@ids", _itemIds.ToArray());
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM doc_lines WHERE doc_id = ANY(@ids);",
                _docIds);
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM docs WHERE id = ANY(@ids);",
                _docIds);
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM order_lines WHERE order_id = ANY(@ids);",
                _orderIds);
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM orders WHERE id = ANY(@ids);",
                _orderIds);
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM partner_item_sale_prices WHERE partner_id = ANY(@ids);",
                _partnerIds);
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM items WHERE id = ANY(@ids);",
                _itemIds);
            await ExecuteDelete(
                connection,
                transaction,
                "DELETE FROM partners WHERE id = ANY(@ids);",
                _partnerIds);
            await transaction.CommitAsync();
        }

        private static async Task ExecuteDelete(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string sql,
            IReadOnlyCollection<long> ids)
        {
            if (ids.Count == 0)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@ids", ids.ToArray());
            await command.ExecuteNonQueryAsync();
        }
    }
}
