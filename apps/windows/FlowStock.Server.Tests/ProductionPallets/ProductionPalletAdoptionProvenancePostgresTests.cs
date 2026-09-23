using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletAdoptionProvenancePostgresTests
{
    [Fact]
    public void Migration_DefinesDurableTransferHistoryWithoutOperationalEntityForeignKeys()
    {
        var sql = File.ReadAllText(GetRepoFilePath(
            "deploy",
            "postgres",
            "migrations",
            "V0036__order_coverage_transfer_provenance.sql"));

        Assert.Contains("CREATE TABLE order_coverage_transfers", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE order_coverage_transfer_lines", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES order_coverage_transfers(id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES order_lines", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES production_pallets", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES docs", sql, StringComparison.Ordinal);
        Assert.Contains("PLANNED_PALLET_ADOPTION", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedAdoption_PersistsMixedLineProvenance_ThatSurvivesSourceLineDeletion()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var affected = store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);

        Assert.Equal(1, affected);
        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(OrderCoverageTransferType.PlannedPalletAdoption, transfer.TransferType);
        Assert.Equal(fixture.SourceOrderId, transfer.SourceOrderId);
        Assert.Equal(fixture.TargetOrderId, transfer.TargetOrderId);
        Assert.Equal(fixture.SourcePrdDocId, transfer.SourcePrdDocId);
        Assert.Equal(fixture.TargetPrdDocId, transfer.TargetPrdDocId);
        Assert.Equal(fixture.ProductionPalletId, transfer.ProductionPalletId);
        Assert.Equal(fixture.HuCode, transfer.HuCode);
        Assert.Equal(ProductionPalletStatus.Planned, transfer.PalletStatusAtTransfer);
        Assert.Equal("IN_PROGRESS", transfer.SourceOrderStatus);
        Assert.Equal(100d, transfer.TransferredQty, 6);
        Assert.Equal(2, transfer.Lines.Count);
        Assert.Equal(
            fixture.SourceOrderLineIds.Order().ToArray(),
            transfer.Lines.Select(line => line.SourceOrderLineId).Order().ToArray());
        Assert.All(transfer.Lines, line =>
        {
            Assert.Equal("INTERNAL_STOCK", line.SourceProductionPurpose);
            Assert.Equal("MIX-1", line.SourceProductionPalletGroup);
            Assert.True(
                line.SourceProductionPalletLineId.HasValue
                && line.SourceProductionPalletLineId.Value > 0);
        });

        fixture.DeleteSourceOrderLines();

        var persisted = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(
            fixture.SourceOrderLineIds.Order().ToArray(),
            persisted.Lines.Select(line => line.SourceOrderLineId).Order().ToArray());
        Assert.Equal(new[] { 40d, 60d }, persisted.Lines.Select(line => line.TransferredQty).Order().ToArray());
    }

    [Fact]
    public void SelectedAdoption_RollsBackProvenanceAndOwnershipTogether()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        Assert.Throws<RollbackRequestedException>(() =>
            store.ExecuteInTransaction(transactionStore =>
            {
                transactionStore.AdoptSelectedProductionPallets(
                    fixture.TargetPrdDocId,
                    fixture.TargetOrderId,
                    [fixture.BuildSelectedAdoption()]);
                throw new RollbackRequestedException();
            }));

        Assert.Empty(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        var ownership = fixture.ReadPalletOwnership();
        Assert.Equal(fixture.SourceOrderId, ownership.OrderId);
        Assert.Equal(fixture.SourcePrdDocId, ownership.PrdDocId);
        Assert.Equal(fixture.SourceOrderLineIds.Single(), ownership.OrderLineId);
    }

    [Fact]
    public void LegacyWholePlanAdoption_PersistsTheSameProvenanceShape()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var result = store.AdoptProductionPalletPlan(
            fixture.SourcePrdDocId,
            fixture.TargetPrdDocId,
            fixture.SourceOrderId,
            fixture.TargetOrderId,
            fixture.TargetOrderLineIdsByItemId);

        Assert.True(result.Success);
        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(fixture.ProductionPalletId, transfer.ProductionPalletId);
        Assert.Equal(100d, transfer.TransferredQty, 6);
        Assert.Equal(2, transfer.Lines.Count);
        Assert.Equal(
            fixture.TargetOrderLineIdsByItemId.Values.Order().ToArray(),
            transfer.Lines.Select(line => line.TargetOrderLineId).Order().ToArray());
    }

    private static string GetRepoFilePath(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {string.Join('/', parts)}");
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
                return value;
            }
        }

        return null;
    }

    private sealed class RollbackRequestedException : Exception;

    private sealed class AdoptionFixture : IDisposable
    {
        private readonly string _connectionString;
        private readonly long _locationId;
        private readonly long[] _itemIds;
        private readonly long[] _sourceDocLineIds;

        private AdoptionFixture(
            string connectionString,
            string token,
            long sourceOrderId,
            long targetOrderId,
            long[] sourceOrderLineIds,
            Dictionary<long, long> targetOrderLineIdsByItemId,
            long sourcePrdDocId,
            long targetPrdDocId,
            long productionPalletId,
            long locationId,
            long[] itemIds,
            long[] sourceDocLineIds,
            string huCode)
        {
            _connectionString = connectionString;
            Token = token;
            SourceOrderId = sourceOrderId;
            TargetOrderId = targetOrderId;
            SourceOrderLineIds = sourceOrderLineIds;
            TargetOrderLineIdsByItemId = targetOrderLineIdsByItemId;
            SourcePrdDocId = sourcePrdDocId;
            TargetPrdDocId = targetPrdDocId;
            ProductionPalletId = productionPalletId;
            _locationId = locationId;
            _itemIds = itemIds;
            _sourceDocLineIds = sourceDocLineIds;
            HuCode = huCode;
        }

        public string Token { get; }
        public long SourceOrderId { get; }
        public long TargetOrderId { get; }
        public long[] SourceOrderLineIds { get; }
        public Dictionary<long, long> TargetOrderLineIdsByItemId { get; }
        public long SourcePrdDocId { get; }
        public long TargetPrdDocId { get; }
        public long ProductionPalletId { get; }
        public string HuCode { get; }

        public static AdoptionFixture Create(string connectionString, bool mixed)
        {
            var token = $"prov-{Guid.NewGuid():N}";
            var huCode = $"{token}-HU";
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            long Scalar(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var parameter in parameters)
                {
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                }

                return Convert.ToInt64(command.ExecuteScalar());
            }

            var item1 = Scalar(
                "INSERT INTO items(name, barcode) VALUES (@name, @barcode) RETURNING id;",
                ("name", $"{token}-item-1"),
                ("barcode", $"{token}-sku-1"));
            var itemIds = new List<long> { item1 };
            if (mixed)
            {
                itemIds.Add(Scalar(
                    "INSERT INTO items(name, barcode) VALUES (@name, @barcode) RETURNING id;",
                    ("name", $"{token}-item-2"),
                    ("barcode", $"{token}-sku-2")));
            }

            var locationId = Scalar(
                "INSERT INTO locations(code, name) VALUES (@code, @name) RETURNING id;",
                ("code", $"{token}-loc"),
                ("name", $"{token}-location"));
            var sourceOrderId = Scalar(
                "INSERT INTO orders(order_ref, order_type, status, created_at) VALUES (@ref, 'INTERNAL', 'IN_PROGRESS', @created) RETURNING id;",
                ("ref", $"{token}-source"),
                ("created", "2042-01-01T00:00:00"));
            var targetOrderId = Scalar(
                "INSERT INTO orders(order_ref, order_type, status, created_at) VALUES (@ref, 'CUSTOMER', 'IN_PROGRESS', @created) RETURNING id;",
                ("ref", $"{token}-target"),
                ("created", "2042-01-01T00:00:00"));

            var sourceLineIds = new List<long>();
            var targetLineIds = new Dictionary<long, long>();
            for (var index = 0; index < itemIds.Count; index++)
            {
                var qty = mixed ? (index == 0 ? 60d : 40d) : 100d;
                var itemId = itemIds[index];
                sourceLineIds.Add(Scalar(
                    @"INSERT INTO order_lines(
    order_id, item_id, qty_ordered, production_purpose, production_pallet_group)
VALUES(@order_id, @item_id, @qty, 'INTERNAL_STOCK', 'MIX-1')
RETURNING id;",
                    ("order_id", sourceOrderId),
                    ("item_id", itemId),
                    ("qty", qty)));
                targetLineIds[itemId] = Scalar(
                    @"INSERT INTO order_lines(
    order_id, item_id, qty_ordered, production_purpose)
VALUES(@order_id, @item_id, @qty, 'CUSTOMER_ORDER')
RETURNING id;",
                    ("order_id", targetOrderId),
                    ("item_id", itemId),
                    ("qty", qty));
            }

            var sourcePrdDocId = Scalar(
                @"INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @created, @order_id, @order_ref)
RETURNING id;",
                ("ref", $"{token}-source-prd"),
                ("created", "2042-01-01T00:00:00"),
                ("order_id", sourceOrderId),
                ("order_ref", $"{token}-source"));
            var targetPrdDocId = Scalar(
                @"INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @created, @order_id, @order_ref)
RETURNING id;",
                ("ref", $"{token}-target-prd"),
                ("created", "2042-01-01T00:00:00"),
                ("order_id", targetOrderId),
                ("order_ref", $"{token}-target"));

            var sourceDocLineIds = new List<long>();
            for (var index = 0; index < itemIds.Count; index++)
            {
                var qty = mixed ? (index == 0 ? 60d : 40d) : 100d;
                sourceDocLineIds.Add(Scalar(
                    @"INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty, to_location_id, to_hu)
VALUES(@doc_id, @order_line_id, 'INTERNAL_STOCK', @item_id, @qty, @location_id, @hu)
RETURNING id;",
                    ("doc_id", sourcePrdDocId),
                    ("order_line_id", sourceLineIds[index]),
                    ("item_id", itemIds[index]),
                    ("qty", qty),
                    ("location_id", locationId),
                    ("hu", huCode)));
            }

            var productionPalletId = Scalar(
                @"INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, created_at)
VALUES(
    @prd_doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, @hu,
    @planned_qty, @location_id, 'PLANNED', @created_at)
RETURNING id;",
                ("prd_doc_id", sourcePrdDocId),
                ("doc_line_id", sourceDocLineIds[0]),
                ("order_id", sourceOrderId),
                ("order_line_id", sourceLineIds[0]),
                ("item_id", itemIds[0]),
                ("hu", huCode),
                ("planned_qty", 100d),
                ("location_id", locationId),
                ("created_at", "2042-01-01T00:00:00"));

            for (var index = 0; index < itemIds.Count; index++)
            {
                var qty = mixed ? (index == 0 ? 60d : 40d) : 100d;
                Scalar(
                    @"INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES(
    @production_pallet_id, @doc_line_id, @order_line_id, @item_id,
    @planned_qty, 0, @created_at)
RETURNING id;",
                    ("production_pallet_id", productionPalletId),
                    ("doc_line_id", sourceDocLineIds[index]),
                    ("order_line_id", sourceLineIds[index]),
                    ("item_id", itemIds[index]),
                    ("planned_qty", qty),
                    ("created_at", "2042-01-01T00:00:00"));
            }

            return new AdoptionFixture(
                connectionString,
                token,
                sourceOrderId,
                targetOrderId,
                sourceLineIds.ToArray(),
                targetLineIds,
                sourcePrdDocId,
                targetPrdDocId,
                productionPalletId,
                locationId,
                itemIds.ToArray(),
                sourceDocLineIds.ToArray(),
                huCode);
        }

        public ProductionPalletSelectedAdoption BuildSelectedAdoption()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            var lines = new List<ProductionPalletSelectedAdoptionLine>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT pll.doc_line_id, pll.order_line_id, pll.item_id, pll.planned_qty
FROM production_pallet_lines pll
WHERE pll.production_pallet_id = @pallet_id
ORDER BY pll.id;";
            command.Parameters.AddWithValue("pallet_id", ProductionPalletId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(2);
                lines.Add(new ProductionPalletSelectedAdoptionLine
                {
                    DocLineId = reader.GetInt64(0),
                    SourceOrderLineId = reader.GetInt64(1),
                    TargetOrderLineId = TargetOrderLineIdsByItemId[itemId],
                    ItemId = itemId,
                    PlannedQty = reader.GetDouble(3)
                });
            }

            return new ProductionPalletSelectedAdoption
            {
                ProductionPalletId = ProductionPalletId,
                SourceOrderId = SourceOrderId,
                SourcePrdDocId = SourcePrdDocId,
                ExpectedStatus = ProductionPalletStatus.Planned,
                HuCode = HuCode,
                TargetOrderLineId = TargetOrderLineIdsByItemId[_itemIds[0]],
                Lines = lines
            };
        }

        public void DeleteSourceOrderLines()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM order_lines WHERE id = ANY(@ids);";
            command.Parameters.AddWithValue("ids", SourceOrderLineIds);
            command.ExecuteNonQuery();
        }

        public (long? OrderId, long PrdDocId, long? OrderLineId) ReadPalletOwnership()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT order_id, prd_doc_id, order_line_id
FROM production_pallets
WHERE id = @id;";
            command.Parameters.AddWithValue("id", ProductionPalletId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }

        public void Dispose()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
DELETE FROM order_coverage_transfers
WHERE source_order_id = @source_order_id OR target_order_id = @target_order_id;

DELETE FROM production_pallet_lines
WHERE production_pallet_id = @pallet_id;

DELETE FROM production_pallets
WHERE id = @pallet_id;

DELETE FROM doc_lines
WHERE id = ANY(@doc_line_ids);

DELETE FROM docs
WHERE id = ANY(@doc_ids);

DELETE FROM order_lines
WHERE order_id = ANY(@order_ids);

DELETE FROM orders
WHERE id = ANY(@order_ids);

DELETE FROM locations
WHERE id = @location_id;

DELETE FROM items
WHERE id = ANY(@item_ids);";
            command.Parameters.AddWithValue("source_order_id", SourceOrderId);
            command.Parameters.AddWithValue("target_order_id", TargetOrderId);
            command.Parameters.AddWithValue("pallet_id", ProductionPalletId);
            command.Parameters.AddWithValue("doc_line_ids", _sourceDocLineIds);
            command.Parameters.AddWithValue("doc_ids", new[] { SourcePrdDocId, TargetPrdDocId });
            command.Parameters.AddWithValue("order_ids", new[] { SourceOrderId, TargetOrderId });
            command.Parameters.AddWithValue("location_id", _locationId);
            command.Parameters.AddWithValue("item_ids", _itemIds);
            command.ExecuteNonQuery();
        }
    }
}
