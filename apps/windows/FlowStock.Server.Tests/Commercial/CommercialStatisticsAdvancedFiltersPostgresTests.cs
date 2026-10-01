using FlowStock.Core.Models;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialStatisticsAdvancedFiltersPostgresTests
{
    [Fact]
    public async Task Sales_filters_name_volume_and_multi_gtin_in_one_authoritative_scope()
    {
        await using var fixture = new Fixture(ResolveRequiredPostgresTestConnectionString());
        var partner = fixture.AddPartner();
        var adzhika190 = fixture.AddItem("Аджика острая", "A190", "АДЖИКА", "190 мл");
        var adzhika500 = fixture.AddItem("Домашняя АДЖИКА", "A500", "АДЖИКА", "500 мл");
        var hren190 = fixture.AddItem("Хрен столовый", "H190", "ХРЕН", "190 мл");

        fixture.AddSale(partner, adzhika190, quantity: 2, unitPriceGross: 100m, new DateTime(2044, 9, 5));
        fixture.AddSale(partner, adzhika500, quantity: 3, unitPriceGross: 120m, new DateTime(2044, 9, 6));
        fixture.AddSale(partner, hren190, quantity: 5, unitPriceGross: 80m, new DateTime(2044, 9, 7));
        fixture.AddSale(partner, adzhika190, quantity: 11, unitPriceGross: 100m, new DateTime(2044, 10, 1));

        var allAdzhika = fixture.Store.GetCommercialStatistics(Query(
            groupBy: CommercialStatisticsGroupBy.Volume,
            itemNameContains: "аДжИкА"));
        Assert.Equal(5m, allAdzhika.Summary.Quantity);
        Assert.Single(allAdzhika.Monthly);
        Assert.Equal("2044-09", allAdzhika.Monthly[0].Month);
        Assert.Equal(2, allAdzhika.Groups.Count);
        Assert.Equal(
            new Dictionary<string, decimal>
            {
                ["190 мл"] = 2m,
                ["500 мл"] = 3m
            },
            allAdzhika.Groups.ToDictionary(row => row.Key!, row => row.Amounts.Quantity));

        var adzhika190Only = fixture.Store.GetCommercialStatistics(Query(
            groupBy: CommercialStatisticsGroupBy.Item,
            itemNameContains: "аджика",
            volume: "190 мл"));
        Assert.Equal(2m, adzhika190Only.Summary.Quantity);
        Assert.Equal(adzhika190.ToString(), Assert.Single(adzhika190Only.Groups).Key);

        var multiGtin = fixture.Store.GetCommercialStatistics(Query(
            groupBy: CommercialStatisticsGroupBy.Gtin,
            gtins: [fixture.Gtin("A190"), fixture.Gtin("H190")]));
        Assert.Equal(7m, multiGtin.Summary.Quantity);
        Assert.Equal(2, multiGtin.Groups.Count);
        var multiGtinKeys = multiGtin.Groups.Select(row => row.Key!).ToHashSet();
        Assert.True(multiGtinKeys.SetEquals([fixture.Gtin("A190"), fixture.Gtin("H190")]));

        var singleAndSetAreOneOrGroup = fixture.Store.GetCommercialStatistics(Query(
            groupBy: CommercialStatisticsGroupBy.Gtin,
            gtin: fixture.Gtin("A500"),
            gtins: [fixture.Gtin("A190")]));
        Assert.Equal(5m, singleAndSetAreOneOrGroup.Summary.Quantity);
        Assert.Equal(2, singleAndSetAreOneOrGroup.Groups.Count);
    }

    private static CommercialStatisticsQuery Query(
        CommercialStatisticsGroupBy groupBy,
        string? gtin = null,
        IReadOnlyList<string>? gtins = null,
        string? itemNameContains = null,
        string? volume = null) =>
        new(
            CommercialStatisticsMode.Sales,
            groupBy,
            From: new DateTime(2044, 9, 1),
            ToExclusive: new DateTime(2044, 10, 1),
            DetailMonth: null,
            PartnerId: null,
            ItemId: null,
            Gtin: gtin,
            Brand: null,
            Volume: volume,
            Statuses: Array.Empty<OrderStatus>(),
            Limit: 100,
            Offset: 0,
            Sort: "name_asc",
            Gtins: gtins,
            ItemNameContains: itemNameContains);

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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly List<long> _partnerIds = [];
        private readonly List<long> _itemIds = [];
        private readonly List<long> _orderIds = [];
        private readonly List<long> _docIds = [];
        private int _saleNumber;

        public Fixture(string connectionString)
        {
            _connectionString = connectionString;
            Prefix = $"CSTAT-AF-{Guid.NewGuid():N}";
            Store = new PostgresDataStore(connectionString);
        }

        public string Prefix { get; }
        public PostgresDataStore Store { get; }
        public string Gtin(string suffix) => $"{Prefix}-{suffix}";

        public long AddPartner()
        {
            var id = Store.AddPartner(new Partner
            {
                Name = $"Клиент {Prefix}",
                Code = Prefix,
                CreatedAt = new DateTime(2044, 1, 1)
            });
            _partnerIds.Add(id);
            return id;
        }

        public long AddItem(string name, string gtinSuffix, string brand, string volume)
        {
            var id = Store.AddItem(new Item
            {
                Name = $"{name} {Prefix}",
                Barcode = $"BAR-{Prefix}-{gtinSuffix}",
                Gtin = Gtin(gtinSuffix),
                BaseUom = "шт",
                Brand = brand,
                Volume = volume
            });
            _itemIds.Add(id);
            return id;
        }

        public void AddSale(
            long partnerId,
            long itemId,
            double quantity,
            decimal unitPriceGross,
            DateTime closedAt)
        {
            var number = ++_saleNumber;
            var orderId = Store.AddOrder(new Order
            {
                OrderRef = $"{Prefix}-ORD-{number}",
                Type = OrderType.Customer,
                PartnerId = partnerId,
                Status = OrderStatus.Accepted,
                CreatedAt = closedAt.AddDays(-1)
            });
            _orderIds.Add(orderId);
            var orderLineId = Store.AddOrderLine(new OrderLine
            {
                OrderId = orderId,
                ItemId = itemId,
                QtyOrdered = quantity,
                UnitPriceGross = unitPriceGross,
                VatRate = 0m,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
            var docId = Store.AddDoc(new Doc
            {
                DocRef = $"{Prefix}-OUT-{number}",
                Type = DocType.Outbound,
                Status = DocStatus.Closed,
                CreatedAt = closedAt.AddHours(-1),
                ClosedAt = closedAt,
                PartnerId = partnerId,
                OrderId = orderId,
                OrderRef = $"{Prefix}-ORD-{number}"
            });
            _docIds.Add(docId);
            Store.AddDocLine(new DocLine
            {
                DocId = docId,
                OrderLineId = orderLineId,
                ItemId = itemId,
                Qty = quantity,
                QtyInput = quantity,
                UomCode = "BASE",
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Delete(connection, transaction, "DELETE FROM doc_lines WHERE doc_id = ANY(@ids);", _docIds);
            await Delete(connection, transaction, "DELETE FROM docs WHERE id = ANY(@ids);", _docIds);
            await Delete(connection, transaction, "DELETE FROM order_lines WHERE order_id = ANY(@ids);", _orderIds);
            await Delete(connection, transaction, "DELETE FROM orders WHERE id = ANY(@ids);", _orderIds);
            await Delete(connection, transaction, "DELETE FROM items WHERE id = ANY(@ids);", _itemIds);
            await Delete(connection, transaction, "DELETE FROM partners WHERE id = ANY(@ids);", _partnerIds);
            await transaction.CommitAsync();
        }

        private static async Task Delete(
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
