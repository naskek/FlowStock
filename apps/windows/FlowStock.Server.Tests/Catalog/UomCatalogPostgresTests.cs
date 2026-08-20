using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Maintenance;
using FlowStock.Server.Tests.Tsd;
using Npgsql;

namespace FlowStock.Server.Tests.Catalog;

public sealed class UomCatalogPostgresTests
{
    [PostgresFact]
    public async Task V0035_AllowsLegacySht_AndRejectsEveryBlockingClass()
    {
        var connectionString = RequiredConnection();
        var migration = ReadRepoFile("deploy", "postgres", "migrations", "V0035__uom_name_normalization_constraints.sql");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var validSchema = await CreateSchema(connection);
        try
        {
            await Execute(connection, $"INSERT INTO {validSchema}.uoms(name) VALUES('Шт.'); INSERT INTO {validSchema}.items(base_uom) VALUES('шт'),(' Шт. ');");
            await ApplyMigration(connection, validSchema, migration);
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                Execute(connection, $"INSERT INTO {validSchema}.uoms(name) VALUES(' шт. ');"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            var blank = await Assert.ThrowsAsync<PostgresException>(() =>
                Execute(connection, $"INSERT INTO {validSchema}.uoms(name) VALUES('   ');"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, blank.SqlState);
        }
        finally
        {
            await DropSchema(connection, validSchema);
        }

        foreach (var fixture in new[]
                 {
                     "INSERT INTO uoms(name) VALUES('   ');",
                     "INSERT INTO uoms(name) VALUES('Кг'),(' кг ');",
                     "INSERT INTO items(base_uom) VALUES('   ');",
                     "INSERT INTO items(base_uom) VALUES('ящик');"
                 })
        {
            var schema = await CreateSchema(connection);
            try
            {
                await ExecuteInSchema(connection, schema, fixture);
                await using var transaction = await connection.BeginTransactionAsync();
                await ExecuteInSchema(connection, transaction, schema, migration, expectFailure: true);
                await transaction.RollbackAsync();
                Assert.Equal(0, await ScalarLong(connection,
                    "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = @schema AND indexname = 'ux_uoms_name_normalized';",
                    schema));
            }
            finally
            {
                await DropSchema(connection, schema);
            }
        }
    }

    [PostgresFact]
    public async Task Remediation_RenameMergeAndMap_MissingMergeSourceIsIdempotentNoOp()
    {
        var baseConnection = RequiredConnection();
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        var schema = await CreateSchema(admin);
        var scopedConnection = WithSearchPath(baseConnection, schema);
        var planPath = Path.Combine(Path.GetTempPath(), $"flowstock-uom-plan-{Guid.NewGuid():N}.json");
        try
        {
            await ExecuteInSchema(admin, schema, @"
INSERT INTO uoms(name) VALUES(''),('Кг'),(' кг '),('Коробка');
INSERT INTO items(base_uom) VALUES(''),('КГ'),(' box '),('шт');");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(new
            {
                rename = new[] { new { uom_id = 1, new_name = "Л" } },
                merge = new[] { new { target_uom_id = 2, source_uom_ids = new[] { 3 }, target_name = "Кг" } },
                map_orphan = new[] { new { source_value = "box", target_uom_id = 4 } }
            }));

            Assert.Equal(3, UomRemediationCommand.Run(scopedConnection, apply: false));
            Assert.Equal(0, UomRemediationCommand.Run(scopedConnection, apply: false, planPath));
            Assert.Equal("", await ScalarString(admin, $"SELECT name FROM {schema}.uoms WHERE id = 1;"));

            Assert.Equal(0, UomRemediationCommand.Run(scopedConnection, apply: true, planPath));
            Assert.Equal(0, UomRemediationCommand.Run(scopedConnection, apply: true, planPath));
            Assert.Equal(0, UomRemediationCommand.Run(scopedConnection, apply: false));
            Assert.Equal(1, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'шт';"));
            Assert.Equal(1, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'Коробка';"));
            Assert.Equal(0, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.uoms WHERE id = 3;"));
            Assert.Equal(1, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.uoms WHERE id = 2 AND name = 'Кг';"));
        }
        finally
        {
            if (File.Exists(planPath)) File.Delete(planPath);
            await DropSchema(admin, schema);
        }
    }

    [PostgresFact]
    public async Task Remediation_CreateAndMapOrphan_CreatesExplicitMasterMapsOnlyMatchesAndIsIdempotent()
    {
        var baseConnection = RequiredConnection();
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        var schema = await CreateSchema(admin);
        var scopedConnection = WithSearchPath(baseConnection, schema);
        var planPath = Path.Combine(Path.GetTempPath(), $"flowstock-uom-create-map-{Guid.NewGuid():N}.json");
        try
        {
            await ExecuteInSchema(admin, schema, @"
INSERT INTO uoms(name) VALUES('Другое');
INSERT INTO items(base_uom) VALUES(' orphan '),('ORPHAN'),('Другое'),('шт');");
            await WritePlan(planPath, new
            {
                create_and_map_orphan = new[]
                {
                    new { source_value = "orphan", new_uom_name = "Ящик" }
                }
            });

            Assert.Equal(0, UomRemediationCommand.Run(scopedConnection, apply: true, planPath));
            Assert.Equal(0, UomRemediationCommand.Run(scopedConnection, apply: true, planPath));

            Assert.Equal(1, await ScalarLong(admin,
                $"SELECT COUNT(*) FROM {schema}.uoms WHERE LOWER(BTRIM(name)) = LOWER(BTRIM('Ящик'));"));
            Assert.Equal(2, await ScalarLong(admin,
                $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'Ящик';"));
            Assert.Equal(1, await ScalarLong(admin,
                $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'Другое';"));
            Assert.Equal(1, await ScalarLong(admin,
                $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'шт';"));
        }
        finally
        {
            if (File.Exists(planPath)) File.Delete(planPath);
            await DropSchema(admin, schema);
        }
    }

    [PostgresFact]
    public async Task Remediation_CreateAndMapOrphan_RejectsLegacySourceAndTargetWithoutMutation()
    {
        var baseConnection = RequiredConnection();
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        var schema = await CreateSchema(admin);
        var scopedConnection = WithSearchPath(baseConnection, schema);
        var sourcePlanPath = Path.Combine(Path.GetTempPath(), $"flowstock-uom-legacy-source-{Guid.NewGuid():N}.json");
        var targetPlanPath = Path.Combine(Path.GetTempPath(), $"flowstock-uom-legacy-target-{Guid.NewGuid():N}.json");
        try
        {
            await ExecuteInSchema(admin, schema, "INSERT INTO items(base_uom) VALUES('шт'),('orphan');");
            await WritePlan(sourcePlanPath, new
            {
                create_and_map_orphan = new[]
                {
                    new { source_value = "шт", new_uom_name = "Ящик" }
                }
            });
            await WritePlan(targetPlanPath, new
            {
                create_and_map_orphan = new[]
                {
                    new { source_value = "orphan", new_uom_name = "шт" }
                }
            });

            Assert.Throws<InvalidDataException>(() =>
                UomRemediationCommand.Run(scopedConnection, apply: true, sourcePlanPath));
            Assert.Throws<InvalidDataException>(() =>
                UomRemediationCommand.Run(scopedConnection, apply: true, targetPlanPath));

            Assert.Equal(0, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.uoms;"));
            Assert.Equal(1, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'шт';"));
            Assert.Equal(1, await ScalarLong(admin, $"SELECT COUNT(*) FROM {schema}.items WHERE base_uom = 'orphan';"));
        }
        finally
        {
            if (File.Exists(sourcePlanPath)) File.Delete(sourcePlanPath);
            if (File.Exists(targetPlanPath)) File.Delete(targetPlanPath);
            await DropSchema(admin, schema);
        }
    }

    [PostgresFact]
    public async Task Remediation_MissingSourceWithRemainingBlocker_RollsBackEntirePlan()
    {
        var baseConnection = RequiredConnection();
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        var schema = await CreateSchema(admin);
        var scopedConnection = WithSearchPath(baseConnection, schema);
        var planPath = Path.Combine(Path.GetTempPath(), $"flowstock-uom-missing-source-{Guid.NewGuid():N}.json");
        try
        {
            await ExecuteInSchema(admin, schema, @"
INSERT INTO uoms(name) VALUES('Коробка');
INSERT INTO items(base_uom) VALUES('remaining-orphan');");
            await WritePlan(planPath, new
            {
                rename = new[] { new { uom_id = 1, new_name = "Коробка новая" } },
                map_orphan = new[] { new { source_value = "missing-source", target_uom_id = 1 } }
            });

            Assert.Equal(3, UomRemediationCommand.Run(scopedConnection, apply: true, planPath));

            Assert.Equal("Коробка", await ScalarString(admin, $"SELECT name FROM {schema}.uoms WHERE id = 1;"));
            Assert.Equal("remaining-orphan", await ScalarString(admin, $"SELECT base_uom FROM {schema}.items LIMIT 1;"));
        }
        finally
        {
            if (File.Exists(planPath)) File.Delete(planPath);
            await DropSchema(admin, schema);
        }
    }

    [PostgresFact]
    public async Task Rename_CascadesCurrentMasterButDoesNotTouchLegacySht()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var oldName = "UOM-OLD-" + suffix;
        var newName = "UOM-NEW-" + suffix;
        var uomId = await InsertUom(connectionString, oldName);
        var itemId = await InsertItem(connectionString, "UOM-ITEM-" + suffix, oldName);
        var legacyItemId = await InsertItem(connectionString, "UOM-LEGACY-" + suffix, "шт");
        try
        {
            new CatalogService(new PostgresDataStore(connectionString)).RenameUom(uomId, newName);
            Assert.Equal(newName, await ScalarString(connectionString, "SELECT base_uom FROM items WHERE id = @id;", itemId));
            Assert.Equal("шт", await ScalarString(connectionString, "SELECT base_uom FROM items WHERE id = @id;", legacyItemId));
            Assert.Equal(newName, await ScalarString(connectionString, "SELECT name FROM uoms WHERE id = @id;", uomId));
        }
        finally
        {
            await Cleanup(connectionString, [itemId, legacyItemId], [uomId]);
        }
    }

    [PostgresFact]
    public async Task Rename_DuplicateAtFinalStepRollsBackReferenceUpdates()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var oldName = "ROLLBACK-OLD-" + suffix;
        var duplicateName = "ROLLBACK-TARGET-" + suffix;
        var oldUomId = await InsertUom(connectionString, oldName);
        var targetUomId = await InsertUom(connectionString, duplicateName);
        var itemId = await InsertItem(connectionString, "ROLLBACK-ITEM-" + suffix, oldName);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Task.Run(() =>
                new CatalogService(new PostgresDataStore(connectionString)).RenameUom(oldUomId, " " + duplicateName.ToLowerInvariant() + " ")));
            Assert.Equal(oldName, await ScalarString(connectionString, "SELECT base_uom FROM items WHERE id = @id;", itemId));
            Assert.Equal(oldName, await ScalarString(connectionString, "SELECT name FROM uoms WHERE id = @id;", oldUomId));
        }
        finally
        {
            await Cleanup(connectionString, [itemId], [oldUomId, targetUomId]);
        }
    }

    [PostgresFact]
    public async Task CatalogCreate_HoldsSharedUomLock_WhileRenameWaitsAndCascadesAfterCommit()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var oldName = "RACE-OLD-" + suffix;
        var newName = "RACE-NEW-" + suffix;
        var barcode = "RACE-ITEM-" + suffix;
        var uomId = await InsertUom(connectionString, oldName);
        try
        {
            using var writerReady = new ManualResetEventSlim();
            using var allowWriterCommit = new ManualResetEventSlim();
            var writerStore = new PostgresDataStore(WithApplicationName(connectionString, $"uom-create-{suffix}"));
            var create = Task.Run(() => Record.Exception(() =>
                writerStore.ExecuteInTransaction(scopedStore =>
                {
                    new CatalogService(scopedStore).CreateItem(
                        "Race item", barcode, null, oldName, null, null, null, null, false);
                    writerReady.Set();
                    Assert.True(allowWriterCommit.Wait(TimeSpan.FromSeconds(10)));
                })));
            Assert.True(writerReady.Wait(TimeSpan.FromSeconds(10)));

            var renameApplicationName = $"uom-rename-after-create-{suffix}";
            var rename = Task.Run(() => Record.Exception(() =>
                new CatalogService(new PostgresDataStore(WithApplicationName(connectionString, renameApplicationName)))
                    .RenameUom(uomId, newName)));
            try
            {
                await WaitUntilSessionWaitsForLock(connectionString, renameApplicationName);
                Assert.False(rename.IsCompleted);
            }
            finally
            {
                allowWriterCommit.Set();
            }

            Assert.Null(await create.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Null(await rename.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(0, await ScalarLong(connectionString,
                "SELECT COUNT(*) FROM items WHERE barcode = @value AND LOWER(BTRIM(base_uom)) = LOWER(BTRIM(@old));",
                barcode,
                oldName));
            Assert.Equal(0, await ScalarLong(connectionString, @"
SELECT COUNT(*) FROM items i
WHERE i.barcode = @value AND LOWER(BTRIM(i.base_uom)) <> 'шт'
  AND NOT EXISTS (SELECT 1 FROM uoms u WHERE LOWER(BTRIM(u.name)) = LOWER(BTRIM(i.base_uom)));", barcode));
        }
        finally
        {
            await CleanupByBarcode(connectionString, barcode, uomId);
        }
    }

    [PostgresFact]
    public async Task CatalogCreate_HoldsSharedUomLock_WhileDeleteWaitsAndRejectsUsedUom()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var uomName = "DELETE-RACE-" + suffix;
        var barcode = "DELETE-RACE-ITEM-" + suffix;
        var uomId = await InsertUom(connectionString, uomName);
        try
        {
            using var writerReady = new ManualResetEventSlim();
            using var allowWriterCommit = new ManualResetEventSlim();
            var writerStore = new PostgresDataStore(WithApplicationName(connectionString, $"uom-delete-create-{suffix}"));
            var create = Task.Run(() => Record.Exception(() =>
                writerStore.ExecuteInTransaction(scopedStore =>
                {
                    new CatalogService(scopedStore).CreateItem(
                        "Delete race", barcode, null, uomName, null, null, null, null, false);
                    writerReady.Set();
                    Assert.True(allowWriterCommit.Wait(TimeSpan.FromSeconds(10)));
                })));
            Assert.True(writerReady.Wait(TimeSpan.FromSeconds(10)));

            var deleteApplicationName = $"uom-delete-after-create-{suffix}";
            var delete = Task.Run(() => Record.Exception(() =>
                new CatalogService(new PostgresDataStore(WithApplicationName(connectionString, deleteApplicationName)))
                    .DeleteUom(uomId)));
            try
            {
                await WaitUntilSessionWaitsForLock(connectionString, deleteApplicationName);
                Assert.False(delete.IsCompleted);
            }
            finally
            {
                allowWriterCommit.Set();
            }

            Assert.Null(await create.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<InvalidOperationException>(await delete.WaitAsync(TimeSpan.FromSeconds(10)));

            var itemCount = await ScalarLong(connectionString, "SELECT COUNT(*) FROM items WHERE barcode = @value;", barcode);
            var uomCount = await ScalarLong(connectionString, "SELECT COUNT(*) FROM uoms WHERE id = CAST(@value AS bigint);", uomId.ToString());
            Assert.Equal(1, itemCount);
            Assert.Equal(1, uomCount);
        }
        finally
        {
            await CleanupByBarcode(connectionString, barcode, uomId);
        }
    }

    [PostgresFact]
    public async Task ImportUpsert_HoldsSharedUomLock_WhileRenameWaitsAndCascadesAfterCommit()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var oldName = "IMPORT-OLD-" + suffix;
        var newName = "IMPORT-NEW-" + suffix;
        var barcode = "IMPORT-UOM-" + suffix;
        var eventId = "IMPORT-UOM-EVENT-" + suffix;
        var uomId = await InsertUom(connectionString, oldName);
        var json = JsonSerializer.Serialize(new
        {
            @event = "ITEM_UPSERT",
            event_id = eventId,
            device_id = "PG-TEST",
            item = new { name = "Import race", barcode, base_uom = oldName }
        });
        try
        {
            using var writerReady = new ManualResetEventSlim();
            using var allowWriterCommit = new ManualResetEventSlim();
            var importStore = new PostgresDataStore(WithApplicationName(connectionString, $"uom-import-{suffix}"));
            var import = Task.Run(() => Record.Exception(() =>
                importStore.ExecuteInTransaction(scopedStore =>
                {
                    var result = new ImportService(scopedStore).ImportJsonlContent(json);
                    Assert.Equal(1, result.ItemsUpserted);
                    writerReady.Set();
                    Assert.True(allowWriterCommit.Wait(TimeSpan.FromSeconds(10)));
                })));
            Assert.True(writerReady.Wait(TimeSpan.FromSeconds(10)));

            var renameApplicationName = $"uom-rename-after-import-{suffix}";
            var rename = Task.Run(() => Record.Exception(() =>
                new CatalogService(new PostgresDataStore(WithApplicationName(connectionString, renameApplicationName)))
                    .RenameUom(uomId, newName)));
            try
            {
                await WaitUntilSessionWaitsForLock(connectionString, renameApplicationName);
                Assert.False(rename.IsCompleted);
            }
            finally
            {
                allowWriterCommit.Set();
            }

            Assert.Null(await import.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Null(await rename.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(0, await ScalarLong(connectionString,
                "SELECT COUNT(*) FROM items WHERE barcode = @value AND LOWER(BTRIM(base_uom)) = LOWER(BTRIM(@old));",
                barcode,
                oldName));
        }
        finally
        {
            await CleanupImport(connectionString, barcode, eventId, uomId);
        }
    }

    [PostgresFact]
    public async Task CatalogUpdate_HoldsSharedUomLock_WhileRenameWaitsAndCascadesAfterCommit()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var oldName = "UPDATE-OLD-" + suffix;
        var newName = "UPDATE-NEW-" + suffix;
        var barcode = "UPDATE-UOM-" + suffix;
        var uomId = await InsertUom(connectionString, oldName);
        var itemId = await InsertItem(connectionString, barcode, oldName);
        try
        {
            using var writerReady = new ManualResetEventSlim();
            using var allowWriterCommit = new ManualResetEventSlim();
            var updateStore = new PostgresDataStore(WithApplicationName(connectionString, $"uom-update-{suffix}"));
            var update = Task.Run(() => Record.Exception(() =>
                updateStore.ExecuteInTransaction(scopedStore =>
                {
                    new CatalogService(scopedStore).UpdateItem(
                        itemId, "Updated item", barcode, null, oldName, null, null, null, null, false);
                    writerReady.Set();
                    Assert.True(allowWriterCommit.Wait(TimeSpan.FromSeconds(10)));
                })));
            Assert.True(writerReady.Wait(TimeSpan.FromSeconds(10)));

            var renameApplicationName = $"uom-rename-after-update-{suffix}";
            var rename = Task.Run(() => Record.Exception(() =>
                new CatalogService(new PostgresDataStore(WithApplicationName(connectionString, renameApplicationName)))
                    .RenameUom(uomId, newName)));
            try
            {
                await WaitUntilSessionWaitsForLock(connectionString, renameApplicationName);
                Assert.False(rename.IsCompleted);
            }
            finally
            {
                allowWriterCommit.Set();
            }

            Assert.Null(await update.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Null(await rename.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(newName, await ScalarString(connectionString, "SELECT base_uom FROM items WHERE id = @id;", itemId));
        }
        finally
        {
            await Cleanup(connectionString, [itemId], [uomId]);
        }
    }

    private static async Task<string> CreateSchema(NpgsqlConnection connection)
    {
        var schema = "uom_test_" + Guid.NewGuid().ToString("N");
        await Execute(connection, $@"
CREATE SCHEMA {schema};
CREATE TABLE {schema}.uoms(id BIGSERIAL PRIMARY KEY, name TEXT NOT NULL);
CREATE INDEX ix_uoms_name ON {schema}.uoms(name);
CREATE TABLE {schema}.items(id BIGSERIAL PRIMARY KEY, base_uom TEXT NOT NULL DEFAULT 'шт');");
        return schema;
    }

    private static async Task ApplyMigration(NpgsqlConnection connection, string schema, string migration)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteInSchema(connection, transaction, schema, migration);
        await transaction.CommitAsync();
    }

    private static async Task ExecuteInSchema(NpgsqlConnection connection, string schema, string sql) =>
        await Execute(connection, $"SET search_path TO {schema}; {sql}; RESET search_path;");

    private static async Task ExecuteInSchema(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string sql,
        bool expectFailure = false)
    {
        await new NpgsqlCommand($"SET LOCAL search_path TO {schema};", connection, transaction).ExecuteNonQueryAsync();
        if (expectFailure)
        {
            await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(sql, connection, transaction).ExecuteNonQueryAsync());
        }
        else
        {
            await new NpgsqlCommand(sql, connection, transaction).ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> InsertUom(string connectionString, string name)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("INSERT INTO uoms(name) VALUES(@name) RETURNING id;", connection);
        command.Parameters.AddWithValue("@name", name);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertItem(string connectionString, string barcode, string baseUom)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("INSERT INTO items(name, barcode, base_uom) VALUES(@name, @barcode, @uom) RETURNING id;", connection);
        command.Parameters.AddWithValue("@name", barcode); command.Parameters.AddWithValue("@barcode", barcode); command.Parameters.AddWithValue("@uom", baseUom);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static string WithSearchPath(string connectionString, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        return builder.ConnectionString;
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

    private static async Task WaitUntilSessionWaitsForLock(string connectionString, string applicationName)
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

        throw new TimeoutException($"Сессия {applicationName} не перешла в ожидание PostgreSQL lock.");
    }

    private static Task WritePlan(string path, object plan) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan));

    private static async Task Execute(NpgsqlConnection connection, string sql) =>
        await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync();

    private static async Task<long> ScalarLong(NpgsqlConnection connection, string sql, string value)
    {
        var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("@schema", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> ScalarLong(string connectionString, string sql, string value, string? old = null)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("@value", value);
        if (old != null) command.Parameters.AddWithValue("@old", old);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> ScalarLong(NpgsqlConnection connection, string sql)
        => Convert.ToInt64(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync() ?? 0L);

    private static async Task<string?> ScalarString(NpgsqlConnection connection, string sql)
    {
        var value = await new NpgsqlCommand(sql, connection).ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task<string?> ScalarString(string connectionString, string sql, long id)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("@id", id);
        var value = await command.ExecuteScalarAsync(); return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task DropSchema(NpgsqlConnection connection, string schema) =>
        await Execute(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");

    private static async Task Cleanup(string connectionString, long[] itemIds, long[] uomIds)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("DELETE FROM items WHERE id = ANY(@items); DELETE FROM uoms WHERE id = ANY(@uoms);", connection);
        command.Parameters.AddWithValue("@items", itemIds); command.Parameters.AddWithValue("@uoms", uomIds); await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupByBarcode(string connectionString, string barcode, long uomId)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("DELETE FROM items WHERE barcode = @barcode; DELETE FROM uoms WHERE id = @uom;", connection);
        command.Parameters.AddWithValue("@barcode", barcode); command.Parameters.AddWithValue("@uom", uomId); await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupImport(string connectionString, string barcode, string eventId, long uomId)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("DELETE FROM imported_events WHERE event_id = @event; DELETE FROM items WHERE barcode = @barcode; DELETE FROM uoms WHERE id = @uom;", connection);
        command.Parameters.AddWithValue("@event", eventId); command.Parameters.AddWithValue("@barcode", barcode); command.Parameters.AddWithValue("@uom", uomId); await command.ExecuteNonQueryAsync();
    }

    private static string RequiredConnection() => TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("Не удалось найти файл репозитория.", Path.Combine(parts));
    }
}
