using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Tests.Tsd;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Server.Tests.ProductionPallets;

[Collection("Production label fingerprint PostgreSQL")]
public sealed class ProductionPalletLabelBackfillPostgresTests
{
    [PostgresFact]
    public async Task V0042_DoesNotNormalizeLifecycleOrCreateFakePrintedAt()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var schema = "label_v0042_" + Guid.NewGuid().ToString("N");
        var migration = ReadRepoFile("deploy", "postgres", "migrations", "V0042__production_pallet_label_fingerprint.sql");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await new NpgsqlCommand($$"""
CREATE SCHEMA {{schema}};
CREATE TABLE {{schema}}.production_pallets (
    id BIGINT PRIMARY KEY,
    status TEXT NOT NULL,
    created_at TEXT NOT NULL,
    printed_at TEXT NULL
);
INSERT INTO {{schema}}.production_pallets(id, status, created_at, printed_at)
VALUES
    (1, 'PRINTED', '2026-08-29T08:00:00', NULL),
    (2, 'PLANNED', '2026-08-29T08:00:00', '2026-08-29T09:00:00');
""", connection).ExecuteNonQueryAsync();

            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await new NpgsqlCommand($"SET LOCAL search_path TO {schema};", connection, transaction).ExecuteNonQueryAsync();
                await new NpgsqlCommand(migration, connection, transaction).ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            var rows = new List<(long Id, string Status, string? PrintedAt)>();
            await using (var command = new NpgsqlCommand(
                             $"SELECT id, status, printed_at FROM {schema}.production_pallets ORDER BY id;",
                             connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            Assert.Equal((1L, "PRINTED", null), rows[0]);
            Assert.Equal((2L, "PLANNED", "2026-08-29T09:00:00"), rows[1]);
        }
        finally
        {
            await new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE;", connection).ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Backfill_DryRunApplyBlockersAndSecondApply_AreFailSafe()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var fixture = await InsertFixture(connectionString);
        try
        {
            var store = new PostgresDataStore(connectionString);
            var palletService = new ProductionPalletService(store);
            var before = palletService.Scan(fixture.OrderId, fixture.DocId, fixture.EligibleHu);
            Assert.False(before.Success);
            Assert.Equal(ProductionFillingErrorCodes.LabelStateUnverified, before.Error);

            var service = new ProductionPalletLabelBackfillService(store);
            var dryRun = service.Run(apply: false, [fixture.OrderId]);
            Assert.Equal(0, dryRun.BackfilledCount);
            Assert.Equal(2, dryRun.BlockerCount);
            Assert.Null(await ReadFingerprint(connectionString, fixture.EligiblePalletId));

            var applied = service.Run(apply: true, [fixture.OrderId]);
            Assert.Equal(1, applied.BackfilledCount);
            Assert.Equal(2, applied.BlockerCount);
            Assert.NotNull(await ReadFingerprint(connectionString, fixture.EligiblePalletId));
            Assert.Null(await ReadFingerprint(connectionString, fixture.NoTimestampPalletId));
            Assert.Null(await ReadFingerprint(connectionString, fixture.PlannedEvidencePalletId));

            var after = palletService.Scan(fixture.OrderId, fixture.DocId, fixture.EligibleHu);
            Assert.True(after.Success, $"{after.Error}: {after.ErrorMessage}");

            var secondApply = service.Run(apply: true, [fixture.OrderId]);
            Assert.Equal(0, secondApply.BackfilledCount);
            Assert.Contains(secondApply.Rows, row => row.PalletId == fixture.EligiblePalletId
                                                     && row.Action == ProductionPalletLabelBackfillAction.AlreadyVerified);
        }
        finally
        {
            await DeleteFixture(connectionString, fixture);
        }
    }

    private static async Task<Fixture> InsertFixture(string connectionString)
    {
        var token = Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var itemId = await InsertId(connection, @"
INSERT INTO items(name, barcode, base_uom, max_qty_per_hu)
VALUES (@name, @barcode, 'шт', 378)
RETURNING id;", ("name", "Label item " + token), ("barcode", "LBL-" + token));
        var locationId = await InsertId(connection, @"
INSERT INTO locations(code, name)
VALUES (@code, @name)
RETURNING id;", ("code", "LBL-" + token), ("name", "Label location " + token));
        var orderId = await InsertId(connection, @"
INSERT INTO orders(order_ref, order_type, status, created_at)
VALUES (@order_ref, 'CUSTOMER', 'IN_PROGRESS', @created_at)
RETURNING id;", ("order_ref", "LBL-" + token), ("created_at", "2026-08-29T08:00:00"));
        var orderLineId = await InsertId(connection, @"
INSERT INTO order_lines(order_id, item_id, qty_ordered, production_purpose)
VALUES (@order_id, @item_id, 1134, 'CUSTOMER_ORDER')
RETURNING id;", ("order_id", orderId), ("item_id", itemId));
        var docId = await InsertId(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES (@doc_ref, 'PRD', 'DRAFT', @created_at, @order_id, @order_ref)
RETURNING id;", ("doc_ref", "PRD-LBL-" + token), ("created_at", "2026-08-29T08:30:00"), ("order_id", orderId), ("order_ref", "LBL-" + token));

        var eligible = await InsertPallet(connection, docId, orderId, orderLineId, itemId, locationId, "HU-ELIGIBLE-" + token, "PRINTED", "2026-08-29T09:00:00");
        var noTimestamp = await InsertPallet(connection, docId, orderId, orderLineId, itemId, locationId, "HU-NO-TIME-" + token, "PRINTED", null);
        var plannedEvidence = await InsertPallet(connection, docId, orderId, orderLineId, itemId, locationId, "HU-PLANNED-" + token, "PLANNED", "2026-08-29T09:05:00");

        return new Fixture(
            token,
            itemId,
            locationId,
            orderId,
            orderLineId,
            docId,
            eligible.PalletId,
            noTimestamp.PalletId,
            plannedEvidence.PalletId,
            eligible.HuCode);
    }

    private static async Task<(long PalletId, string HuCode)> InsertPallet(
        NpgsqlConnection connection,
        long docId,
        long orderId,
        long orderLineId,
        long itemId,
        long locationId,
        string huCode,
        string status,
        string? printedAt)
    {
        var docLineId = await InsertId(connection, @"
INSERT INTO doc_lines(doc_id, order_line_id, production_purpose, item_id, qty, to_location_id, to_hu, pack_single_hu)
VALUES (@doc_id, @order_line_id, 'CUSTOMER_ORDER', @item_id, 378, @location_id, @hu, TRUE)
RETURNING id;", ("doc_id", docId), ("order_line_id", orderLineId), ("item_id", itemId), ("location_id", locationId), ("hu", huCode));

        await using var command = new NpgsqlCommand(@"
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, printed_at, created_at)
VALUES (
    @doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, @hu,
    378, @location_id, @status, @printed_at, @created_at)
RETURNING id;", connection);
        command.Parameters.AddWithValue("doc_id", docId);
        command.Parameters.AddWithValue("doc_line_id", docLineId);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("order_line_id", orderLineId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("hu", huCode);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("printed_at", NpgsqlDbType.Text) { Value = printedAt ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("created_at", "2026-08-29T08:45:00");
        var palletId = Convert.ToInt64(await command.ExecuteScalarAsync());
        return (palletId, huCode);
    }

    private static async Task<long> InsertId(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadFingerprint(string connectionString, long palletId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT printed_label_fingerprint FROM production_pallets WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", palletId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task DeleteFixture(string connectionString, Fixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
DELETE FROM production_pallet_lines WHERE production_pallet_id IN (
    SELECT id FROM production_pallets WHERE order_id = @order_id);
DELETE FROM production_pallets WHERE order_id = @order_id;
DELETE FROM doc_lines WHERE doc_id = @doc_id;
DELETE FROM docs WHERE id = @doc_id;
DELETE FROM order_lines WHERE order_id = @order_id;
DELETE FROM orders WHERE id = @order_id;
DELETE FROM locations WHERE id = @location_id;
DELETE FROM items WHERE id = @item_id;", connection);
        command.Parameters.AddWithValue("order_id", fixture.OrderId);
        command.Parameters.AddWithValue("doc_id", fixture.DocId);
        command.Parameters.AddWithValue("location_id", fixture.LocationId);
        command.Parameters.AddWithValue("item_id", fixture.ItemId);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(parts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Migration file was not found.", Path.Combine(parts));
    }

    private sealed record Fixture(
        string Token,
        long ItemId,
        long LocationId,
        long OrderId,
        long OrderLineId,
        long DocId,
        long EligiblePalletId,
        long NoTimestampPalletId,
        long PlannedEvidencePalletId,
        string EligibleHu);
}

[CollectionDefinition("Production label fingerprint PostgreSQL", DisableParallelization = true)]
public sealed class ProductionPalletLabelBackfillPostgresCollection;
