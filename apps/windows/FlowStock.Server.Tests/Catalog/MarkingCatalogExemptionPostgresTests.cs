using Npgsql;
using FlowStock.Server.Tests.Tsd;

namespace FlowStock.Server.Tests.Catalog;

public sealed class MarkingCatalogExemptionPostgresTests
{
    [PostgresFact]
    public async Task CatalogAndOrderMutation_ShareItemThenOrderLockOrder_WithoutLostStatusRefresh()
    {
        var connectionString = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")
            ?? throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required.");
        var suffix = Guid.NewGuid().ToString("N");
        var gtin = Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString();
        long typeId;
        long itemId;
        long orderId;
        await using (var seed = new NpgsqlConnection(connectionString))
        {
            await seed.OpenAsync();
            await using var command = new NpgsqlCommand("""
                WITH type_row AS (
                    INSERT INTO item_types(name, code, enable_marking)
                    VALUES(@suffix || '-type', @suffix || '-type', TRUE) RETURNING id
                ), item_row AS (
                    INSERT INTO items(name, barcode, gtin, base_uom, item_type_id)
                    SELECT @suffix || '-item', @suffix || '-item', @gtin, 'шт', id
                    FROM type_row RETURNING id, item_type_id
                ), order_row AS (
                    INSERT INTO orders(order_ref, order_type, status, marking_status, created_at)
                    VALUES(@suffix || '-order', 'CUSTOMER', 'ACCEPTED', 'NOT_APPLIED',
                           '2026-08-24T00:00:00.000Z') RETURNING id
                ), line_row AS (
                    INSERT INTO order_lines(order_id, item_id, qty_ordered)
                    SELECT order_row.id, item_row.id, 10 FROM order_row CROSS JOIN item_row
                )
                SELECT type_row.id, item_row.id, order_row.id
                FROM type_row CROSS JOIN item_row CROSS JOIN order_row;
                """, seed);
            command.Parameters.AddWithValue("suffix", suffix);
            command.Parameters.AddWithValue("gtin", gtin);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            typeId = reader.GetInt64(0);
            itemId = reader.GetInt64(1);
            orderId = reader.GetInt64(2);
        }

        try
        {
            await using var catalogConnection = new NpgsqlConnection(connectionString);
            await using var orderConnection = new NpgsqlConnection(connectionString);
            await catalogConnection.OpenAsync();
            await orderConnection.OpenAsync();
            await using var catalogTransaction = await catalogConnection.BeginTransactionAsync();
            await using var orderTransaction = await orderConnection.BeginTransactionAsync();

            await new NpgsqlCommand(
                "UPDATE items SET chz_marking_exempt = TRUE WHERE id = @id;",
                catalogConnection,
                catalogTransaction)
            {
                Parameters = { new("id", itemId) }
            }.ExecuteNonQueryAsync();

            var orderMutation = Task.Run(async () =>
            {
                await new NpgsqlCommand(
                    "SELECT id FROM items WHERE id = @item_id FOR UPDATE;",
                    orderConnection,
                    orderTransaction)
                {
                    Parameters = { new("item_id", itemId) }
                }.ExecuteNonQueryAsync();
                await new NpgsqlCommand(
                    "SELECT id FROM orders WHERE id = @order_id FOR UPDATE;",
                    orderConnection,
                    orderTransaction)
                {
                    Parameters = { new("order_id", orderId) }
                }.ExecuteNonQueryAsync();
                await new NpgsqlCommand(
                    "UPDATE orders SET marking_status = calculate_order_marking_status(id) WHERE id = @order_id;",
                    orderConnection,
                    orderTransaction)
                {
                    Parameters = { new("order_id", orderId) }
                }.ExecuteNonQueryAsync();
                await orderTransaction.CommitAsync();
            });

            await Task.Delay(100);
            Assert.False(orderMutation.IsCompleted);
            await catalogTransaction.CommitAsync();
            await orderMutation.WaitAsync(TimeSpan.FromSeconds(5));

            await using var assertion = new NpgsqlConnection(connectionString);
            await assertion.OpenAsync();
            var status = Convert.ToString(await new NpgsqlCommand(
                "SELECT marking_status FROM orders WHERE id = @id AND marking_status = calculate_order_marking_status(id);",
                assertion)
            {
                Parameters = { new("id", orderId) }
            }.ExecuteScalarAsync());
            Assert.Equal("NOT_REQUIRED", status);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await new NpgsqlCommand("""
                DELETE FROM order_lines WHERE order_id = @order_id;
                DELETE FROM orders WHERE id = @order_id;
                DELETE FROM items WHERE id = @item_id;
                DELETE FROM item_types WHERE id = @type_id;
                """, cleanup)
            {
                Parameters =
                {
                    new("order_id", orderId),
                    new("item_id", itemId),
                    new("type_id", typeId)
                }
            }.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task ApplicabilityTransitions_AreGtinSafe_AndRefreshPersistedOrderStatus()
    {
        await InRollbackTransaction(async (connection, transaction) =>
        {
            var markingType = await ScalarLong(connection, transaction, """
                INSERT INTO item_types(name, code, enable_marking)
                VALUES ('V0038 marking', @suffix || '-MARK', FALSE)
                RETURNING id;
                """);
            var item = await ScalarLong(connection, transaction, """
                INSERT INTO items(name, barcode, base_uom, item_type_id)
                VALUES ('V0038 legacy invalid', @suffix || '-ITEM', 'шт', @type_id)
                RETURNING id;
                """, ("type_id", markingType));

            await AssertDatabaseError(connection, transaction, "gtin_enable", "MARKING_GTIN_REQUIRED",
                "UPDATE item_types SET enable_marking = TRUE WHERE id = @id;", ("id", markingType));

            await Execute(connection, transaction,
                "UPDATE items SET chz_marking_exempt = TRUE WHERE id = @id;",
                ("id", item));
            await Execute(connection, transaction,
                "UPDATE item_types SET enable_marking = TRUE WHERE id = @id;",
                ("id", markingType));
            await AssertDatabaseError(connection, transaction, "remove_exemption", "MARKING_GTIN_REQUIRED",
                "UPDATE items SET chz_marking_exempt = FALSE WHERE id = @id;", ("id", item));

            await Execute(connection, transaction,
                "UPDATE items SET gtin = '04600000003801', chz_marking_exempt = FALSE WHERE id = @id;",
                ("id", item));
            await AssertDatabaseError(connection, transaction, "clear_gtin", "MARKING_GTIN_REQUIRED",
                "UPDATE items SET gtin = NULL WHERE id = @id;", ("id", item));

            var orderId = await ScalarLong(connection, transaction, """
                INSERT INTO orders(order_ref, order_type, status, marking_status, created_at)
                VALUES (@suffix || '-ORDER', 'CUSTOMER', 'ACCEPTED', 'NOT_APPLIED', '2026-08-24T00:00:00.000Z')
                RETURNING id;
                """);
            await Execute(connection, transaction,
                "INSERT INTO order_lines(order_id, item_id, qty_ordered) VALUES(@order_id, @item_id, 10);",
                ("order_id", orderId), ("item_id", item));

            await Execute(connection, transaction,
                "UPDATE items SET chz_marking_exempt = TRUE WHERE id = @id;", ("id", item));
            Assert.Equal("NOT_REQUIRED", await ScalarString(connection, transaction,
                "SELECT marking_status FROM orders WHERE id = @id;", ("id", orderId)));

            var secondItem = await ScalarLong(connection, transaction, """
                INSERT INTO items(name, barcode, gtin, base_uom, item_type_id)
                VALUES ('V0038 second', @suffix || '-ITEM-2', '04600000003802', 'шт', @type_id)
                RETURNING id;
                """, ("type_id", markingType));
            await Execute(connection, transaction,
                "INSERT INTO order_lines(order_id, item_id, qty_ordered) VALUES(@order_id, @item_id, 5);",
                ("order_id", orderId), ("item_id", secondItem));
            await Execute(connection, transaction,
                "UPDATE orders SET marking_status = calculate_order_marking_status(id) WHERE id = @id;",
                ("id", orderId));

            Assert.Equal("NOT_APPLIED", await ScalarString(connection, transaction,
                "SELECT marking_status FROM orders WHERE id = @id;", ("id", orderId)));
        });
    }

    [PostgresFact]
    public async Task AllApplicabilityDisablePaths_AreBlockedByProtectedLineage()
    {
        await InRollbackTransaction(async (connection, transaction) =>
        {
            var markingType = await InsertType(connection, transaction, true, "MARK");
            var plainType = await InsertType(connection, transaction, false, "PLAIN");
            var item = await ScalarLong(connection, transaction, """
                INSERT INTO items(name, barcode, gtin, base_uom, item_type_id)
                VALUES ('V0038 guarded', @suffix || '-GUARDED', '04600000003803', 'шт', @type_id)
                RETURNING id;
                """, ("type_id", markingType));
            await Execute(connection, transaction, """
                INSERT INTO marking_production_subject(
                    id, lifecycle, item_id, gtin, subject_quantity, created_at)
                VALUES ('00380000-0000-0000-0000-000000000003', 'ACTIVE', @item_id,
                        '04600000003803', 10, '2026-08-24T00:00:00.000Z');
                """, ("item_id", item));

            await AssertDatabaseError(connection, transaction, "exempt", "MARKING_APPLICABILITY_LINEAGE_EXISTS",
                "UPDATE items SET chz_marking_exempt = TRUE WHERE id = @id;", ("id", item));
            await AssertDatabaseError(connection, transaction, "type_change", "MARKING_APPLICABILITY_LINEAGE_EXISTS",
                "UPDATE items SET item_type_id = @type_id WHERE id = @id;", ("type_id", plainType), ("id", item));
            await AssertDatabaseError(connection, transaction, "disable_type", "MARKING_APPLICABILITY_LINEAGE_EXISTS",
                "UPDATE item_types SET enable_marking = FALSE WHERE id = @id;", ("id", markingType));
        });
    }

    [PostgresFact]
    public async Task ParentAndSubjectEvidence_CannotBeApprovedTwice()
    {
        await InRollbackTransaction(async (connection, transaction) =>
        {
            var type = await InsertType(connection, transaction, true, "APPROVAL");
            var item = await ScalarLong(connection, transaction, """
                INSERT INTO items(name, barcode, gtin, base_uom, item_type_id)
                VALUES ('V0038 approval', @suffix || '-APPROVAL', '04600000003804', 'шт', @type_id)
                RETURNING id;
                """, ("type_id", type));
            var orderId = await ScalarLong(connection, transaction, """
                INSERT INTO orders(order_ref, order_type, status, created_at)
                VALUES (@suffix || '-APPROVAL-ORDER', 'INTERNAL', 'ACCEPTED', '2026-08-24T00:00:00.000Z') RETURNING id;
                """);
            var lineId = await ScalarLong(connection, transaction, """
                INSERT INTO order_lines(order_id, item_id, qty_ordered)
                VALUES(@order_id, @item_id, 10) RETURNING id;
                """, ("order_id", orderId), ("item_id", item));
            await Execute(connection, transaction, """
                INSERT INTO marking_production_subject(
                    id, lifecycle, current_order_id, current_order_line_id, item_id, gtin,
                    subject_quantity, created_at)
                VALUES ('00380000-0000-0000-0000-000000000004', 'ACTIVE', @order_id, @line_id,
                        @item_id, '04600000003804', 10, '2026-08-24T00:00:00.000Z');
                """, ("order_id", orderId), ("line_id", lineId), ("item_id", item));
            var parentId = await ScalarLong(connection, transaction, """
                INSERT INTO marking_synthetic_legacy_allowlist(
                    order_line_id, allowed_synthetic_qty, target_qty_at_cutover,
                    approved_at, approved_by, preflight_hash)
                VALUES(@line_id, 10, 10, '2026-08-24T00:00:00.000Z', 'test', 'h1')
                RETURNING id;
                """, ("line_id", lineId));

            await AssertDatabaseError(connection, transaction, "duplicate_parent", PostgresErrorCodes.UniqueViolation,
                """
                INSERT INTO marking_synthetic_legacy_allowlist(
                    order_line_id, allowed_synthetic_qty, target_qty_at_cutover,
                    approved_at, approved_by, preflight_hash)
                VALUES(@line_id, 10, 10, '2026-08-24T00:00:01.000Z', 'test', 'h2');
                """, ("line_id", lineId));
            await Execute(connection, transaction, """
                INSERT INTO marking_synthetic_legacy_allowlist_subject(
                    id, allowlist_id, marking_subject_id, item_id_snapshot, gtin_snapshot,
                    subject_revision_at_cutover, subject_quantity_at_cutover,
                    approved_quantity, preflight_hash, approved_at, approved_by)
                VALUES('00380000-0000-0000-0000-000000000104', @parent_id,
                       '00380000-0000-0000-0000-000000000004', @item_id, '04600000003804',
                       0, 10, 10, 'h2', '2026-08-24T00:00:02.000Z', 'test');
                """, ("parent_id", parentId), ("item_id", item));
            await AssertDatabaseError(connection, transaction, "duplicate_subject", PostgresErrorCodes.UniqueViolation,
                """
                INSERT INTO marking_synthetic_legacy_allowlist_subject(
                    id, allowlist_id, marking_subject_id, item_id_snapshot, gtin_snapshot,
                    subject_revision_at_cutover, subject_quantity_at_cutover,
                    approved_quantity, preflight_hash, approved_at, approved_by)
                VALUES('00380000-0000-0000-0000-000000000204', @parent_id,
                       '00380000-0000-0000-0000-000000000004', @item_id, '04600000003804',
                       0, 10, 0, 'h3', '2026-08-24T00:00:03.000Z', 'test');
                """, ("parent_id", parentId), ("item_id", item));

            await Execute(connection, transaction, """
                INSERT INTO marking_grandfather_operational_allowance(
                    id, allowlist_subject_id, marking_subject_id, approved_quantity,
                    usable_quantity_cap, cutover_subject_revision, preflight_hash, created_at)
                VALUES('00380000-0000-0000-0000-000000000304',
                       '00380000-0000-0000-0000-000000000104',
                       '00380000-0000-0000-0000-000000000004', 10, 10, 0, 'h2',
                       '2026-08-24T00:00:04.000Z');
                INSERT INTO marking_operational_coverage(
                    id, marking_subject_id, source_type, covered_quantity,
                    grandfather_allowance_id, created_at)
                VALUES('00380000-0000-0000-0000-000000000404',
                       '00380000-0000-0000-0000-000000000004',
                       'GRANDFATHER_ALLOWANCE', 10,
                       '00380000-0000-0000-0000-000000000304',
                       '2026-08-24T00:00:05.000Z');
                UPDATE marking_production_subject
                SET subject_quantity = 6
                WHERE id = '00380000-0000-0000-0000-000000000004';
                UPDATE marking_operational_coverage_consumption
                SET active_quantity = 0, retired_at = '2026-08-24T00:00:06.000Z',
                    retirement_reason = 'controlled_correction_supersession'
                WHERE operational_coverage_id = '00380000-0000-0000-0000-000000000404';
                UPDATE marking_operational_coverage
                SET retired_at = '2026-08-24T00:00:06.000Z',
                    retirement_reason = 'controlled_correction_supersession'
                WHERE id = '00380000-0000-0000-0000-000000000404';
                INSERT INTO marking_production_subject(
                    id, lifecycle, predecessor_subject_id, current_order_id, current_order_line_id,
                    item_id, gtin, subject_quantity, created_at)
                VALUES('00380000-0000-0000-0000-000000000504', 'ACTIVE',
                       '00380000-0000-0000-0000-000000000004', @order_id, @line_id,
                       @item_id, '04600000003804', 6, '2026-08-24T00:00:07.000Z');
                """, ("order_id", orderId), ("line_id", lineId), ("item_id", item));

            Assert.Equal("6.000000", await ScalarString(connection, transaction, """
                SELECT usable_quantity_cap::text
                FROM marking_grandfather_operational_allowance
                WHERE id = '00380000-0000-0000-0000-000000000304';
                """));
            await AssertDatabaseError(connection, transaction, "successor_excess", "MARKING_GRANDFATHER_COVERAGE_EXCEEDS_USABLE_CAPACITY",
                """
                INSERT INTO marking_operational_coverage(
                    id, marking_subject_id, source_type, covered_quantity,
                    grandfather_allowance_id, created_at)
                VALUES('00380000-0000-0000-0000-000000000604',
                       '00380000-0000-0000-0000-000000000504', 'GRANDFATHER_ALLOWANCE', 7,
                       '00380000-0000-0000-0000-000000000304', '2026-08-24T00:00:08.000Z');
                """);
            await Execute(connection, transaction, """
                INSERT INTO marking_operational_coverage(
                    id, marking_subject_id, source_type, covered_quantity,
                    grandfather_allowance_id, created_at)
                VALUES('00380000-0000-0000-0000-000000000704',
                       '00380000-0000-0000-0000-000000000504', 'GRANDFATHER_ALLOWANCE', 6,
                       '00380000-0000-0000-0000-000000000304', '2026-08-24T00:00:09.000Z');
                UPDATE marking_production_subject
                SET subject_quantity = 4
                WHERE id = '00380000-0000-0000-0000-000000000504';
                """);
            Assert.Equal("4.000000", await ScalarString(connection, transaction, """
                SELECT usable_quantity_cap::text
                FROM marking_grandfather_operational_allowance
                WHERE id = '00380000-0000-0000-0000-000000000304';
                """));
            await Execute(connection, transaction, """
                UPDATE marking_operational_coverage_consumption
                SET active_quantity = 0, retired_at = '2026-08-24T00:00:10.000Z'
                WHERE operational_coverage_id = '00380000-0000-0000-0000-000000000704';
                UPDATE marking_operational_coverage
                SET retired_at = '2026-08-24T00:00:10.000Z'
                WHERE id = '00380000-0000-0000-0000-000000000704';
                INSERT INTO marking_production_subject(
                    id, lifecycle, current_order_id, current_order_line_id,
                    item_id, gtin, subject_quantity, created_at)
                VALUES('00380000-0000-0000-0000-000000000804', 'ACTIVE', @order_id, @line_id,
                       @item_id, '04600000003804', 6, '2026-08-24T00:00:11.000Z');
                """, ("order_id", orderId), ("line_id", lineId), ("item_id", item));
            await AssertDatabaseError(connection, transaction, "successor_reduced_capacity", "MARKING_GRANDFATHER_COVERAGE_EXCEEDS_USABLE_CAPACITY",
                """
                INSERT INTO marking_operational_coverage(
                    id, marking_subject_id, source_type, covered_quantity,
                    grandfather_allowance_id, created_at)
                VALUES('00380000-0000-0000-0000-000000001104',
                       '00380000-0000-0000-0000-000000000504', 'GRANDFATHER_ALLOWANCE', 5,
                       '00380000-0000-0000-0000-000000000304', '2026-08-24T00:00:11.500Z');
                """);
            await AssertDatabaseError(connection, transaction, "unrelated_subject", "MARKING_GRANDFATHER_ALLOWANCE_SUBJECT_LINEAGE_MISMATCH",
                """
                INSERT INTO marking_operational_coverage(
                    id, marking_subject_id, source_type, covered_quantity,
                    grandfather_allowance_id, created_at)
                VALUES('00380000-0000-0000-0000-000000000904',
                       '00380000-0000-0000-0000-000000000804', 'GRANDFATHER_ALLOWANCE', 4,
                       '00380000-0000-0000-0000-000000000304', '2026-08-24T00:00:12.000Z');
                """);

            await Execute(connection, transaction, """
                UPDATE marking_production_subject SET lifecycle = 'CANCELLED'
                WHERE id = '00380000-0000-0000-0000-000000000504';
                INSERT INTO marking_production_subject(
                    id, lifecycle, predecessor_subject_id, current_order_id, current_order_line_id,
                    item_id, gtin, subject_quantity, created_at)
                VALUES('00380000-0000-0000-0000-000000001204', 'ACTIVE',
                       '00380000-0000-0000-0000-000000000504', @order_id, @line_id,
                       @item_id, '04600000003804', 4, '2026-08-24T00:00:12.500Z');
                """, ("order_id", orderId), ("line_id", lineId), ("item_id", item));
            await AssertDatabaseError(connection, transaction, "cancelled_capacity", "MARKING_GRANDFATHER_COVERAGE_EXCEEDS_USABLE_CAPACITY",
                """
                INSERT INTO marking_operational_coverage(
                    id, marking_subject_id, source_type, covered_quantity,
                    grandfather_allowance_id, created_at)
                VALUES('00380000-0000-0000-0000-000000001004',
                       '00380000-0000-0000-0000-000000001204', 'GRANDFATHER_ALLOWANCE', 1,
                       '00380000-0000-0000-0000-000000000304', '2026-08-24T00:00:13.000Z');
                """);
        });
    }

    private static async Task<long> InsertType(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool enableMarking,
        string label)
        => await ScalarLong(connection, transaction, """
            INSERT INTO item_types(name, code, enable_marking)
            VALUES ('V0038 ' || @label, @suffix || '-' || @label, @enabled)
            RETURNING id;
            """, ("label", label), ("enabled", enableMarking));

    private static async Task InRollbackTransaction(Func<NpgsqlConnection, NpgsqlTransaction, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")
            ?? throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await test(connection, transaction);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task AssertDatabaseError(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string savepoint,
        string expected,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await transaction.SaveAsync(savepoint);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            Execute(connection, transaction, sql, parameters));
        Assert.True(
            error.MessageText.Contains(expected, StringComparison.Ordinal)
            || string.Equals(error.SqlState, expected, StringComparison.Ordinal),
            $"Expected '{expected}', got '{error.SqlState}: {error.MessageText}'.");
        await transaction.RollbackAsync(savepoint);
    }

    private static async Task<long> ScalarLong(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
        => Convert.ToInt64(await Command(connection, transaction, sql, parameters).ExecuteScalarAsync());

    private static async Task<string?> ScalarString(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
        => Convert.ToString(await Command(connection, transaction, sql, parameters).ExecuteScalarAsync());

    private static async Task Execute(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
        => _ = await Command(connection, transaction, sql, parameters).ExecuteNonQueryAsync();

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("suffix", Guid.NewGuid().ToString("N"));
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }
}
