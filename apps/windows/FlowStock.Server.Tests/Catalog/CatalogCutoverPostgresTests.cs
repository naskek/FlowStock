using System.Security.Cryptography;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Maintenance;
using FlowStock.Server.Tests.Tsd;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowStock.Server.Tests.Catalog;

[Collection(CatalogCutoverPostgresTestCollection.Name)]
public sealed class CatalogCutoverPostgresTests
{
    [PostgresFact]
    public async Task ConcurrentAdminLogins_LeaveExactlyOneResolvableSession()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var login = $"catalog-admin-{suffix}";
        var accountId = await CreateAccount(connectionString, login, "password", PcAccessRole.Admin);
        try
        {
            using var start = new ManualResetEventSlim();
            var first = Task.Run(() => { start.Wait(); return new PcWebSessionStore(connectionString).Login(login, "password", DateTimeOffset.UtcNow); });
            var second = Task.Run(() => { start.Wait(); return new PcWebSessionStore(connectionString).Login(login, "password", DateTimeOffset.UtcNow.AddMilliseconds(1)); });
            start.Set();
            var results = await Task.WhenAll(first, second);

            Assert.All(results, result => Assert.True(result.IsSuccess));
            Assert.Equal(1, await ScalarLong(connectionString, "SELECT COUNT(*) FROM pc_web_sessions WHERE account_id = @id AND revoked_at IS NULL;", accountId));
            var resolved = results.Count(result => Resolve(connectionString, result.Token!) != null);
            Assert.Equal(1, resolved);
        }
        finally
        {
            await DeleteAccount(connectionString, accountId);
        }
    }

    [PostgresFact]
    public async Task OperatorPromotionToAdmin_RevokesPreviouslyOpenSessions()
    {
        var connectionString = RequiredConnection();
        var login = "catalog-promotion-" + Guid.NewGuid().ToString("N");
        var accountId = await CreateAccount(connectionString, login, "password", PcAccessRole.Operator);
        try
        {
            var session = new PcWebSessionStore(connectionString).Login(login, "password", DateTimeOffset.UtcNow);
            Assert.True(session.IsSuccess);
            Assert.NotNull(Resolve(connectionString, session.Token!));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var promote = new NpgsqlCommand("UPDATE tsd_devices SET access_role = 'ADMIN' WHERE id = @id;", connection, transaction);
            promote.Parameters.AddWithValue("@id", accountId);
            await promote.ExecuteNonQueryAsync();
            PcWebSessionStore.RevokeForAdminPromotion(connection, transaction, accountId, DateTimeOffset.UtcNow);
            await transaction.CommitAsync();

            Assert.Null(Resolve(connectionString, session.Token!));
        }
        finally
        {
            await DeleteAccount(connectionString, accountId);
        }
    }

    [PostgresFact]
    public async Task PartnerRole_UsesDatabaseThenJsonFallback_AndBackfillDoesNotOverwrite()
    {
        var connectionString = RequiredConnection();
        var ids = await InsertPartners(connectionString, 4);
        var legacyPath = Path.Combine(Path.GetTempPath(), $"flowstock-partner-roles-{Guid.NewGuid():N}.json");
        try
        {
            await Execute(connectionString, "UPDATE partners SET partner_role = 'CLIENT' WHERE id = @id;", ids[3]);
            await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new Dictionary<long, string>
            {
                [ids[0]] = "Client",
                [ids[1]] = "Supplier",
                [long.MaxValue] = "Supplier"
            }));
            var store = new PostgresDataStore(connectionString);
            var resolver = new PartnerRoleResolver(store, legacyPath);
            Assert.Equal(FlowStockPartnerRole.Client, resolver.GetRole(ids[0]));
            Assert.Equal(FlowStockPartnerRole.Supplier, resolver.GetRole(ids[1]));
            Assert.Equal(FlowStockPartnerRole.Both, resolver.GetRole(ids[2]));
            Assert.Equal(FlowStockPartnerRole.Client, resolver.GetRole(ids[3]));

            var malformedPath = legacyPath + ".malformed";
            await File.WriteAllTextAsync(malformedPath, "{not-json");
            Assert.Equal(2, PartnerRoleBackfillCommand.Run(connectionString, apply: true, malformedPath));
            File.Delete(malformedPath);

            new CatalogService(store).UpdatePartner(ids[0], "Изменённый", null, "BOTH");
            Assert.Equal("BOTH", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[0]));

            Assert.Equal(0, PartnerRoleBackfillCommand.Run(connectionString, apply: false, legacyPath));
            Assert.Null(await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[2]));
            Assert.Equal(0, PartnerRoleBackfillCommand.Run(connectionString, apply: true, legacyPath));
            Assert.Equal("BOTH", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[0]));
            Assert.Equal("SUPPLIER", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[1]));
            Assert.Equal("BOTH", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[2]));
            Assert.Equal("CLIENT", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[3]));
            Assert.Equal(0, await ScalarLong(connectionString, "SELECT COUNT(*) FROM partners WHERE id = ANY(@ids) AND partner_role IS NULL;", ids));
        }
        finally
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            await DeletePartners(connectionString, ids);
        }
    }

    [PostgresFact]
    public async Task PartnerRoleBackfill_UnknownNumericLegacyRole_IsMalformedAndDoesNotChangeDatabase()
    {
        var connectionString = RequiredConnection();
        var ids = await InsertPartners(connectionString, 1);
        var legacyPath = Path.Combine(Path.GetTempPath(), $"flowstock-partner-roles-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(legacyPath, $$"""{"{{ids[0]}}":999}""");

            Assert.Equal(2, PartnerRoleBackfillCommand.Run(connectionString, apply: false, legacyPath));
            Assert.Equal(2, PartnerRoleBackfillCommand.Run(connectionString, apply: true, legacyPath));
            Assert.Null(await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[0]));
        }
        finally
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            await DeletePartners(connectionString, ids);
        }
    }

    [PostgresFact]
    public async Task PartnerRoleBackfill_UnknownStringLegacyRole_IsMalformedAndDoesNotChangeDatabase()
    {
        var connectionString = RequiredConnection();
        var ids = await InsertPartners(connectionString, 1);
        var legacyPath = Path.Combine(Path.GetTempPath(), $"flowstock-partner-roles-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(legacyPath, $$"""{"{{ids[0]}}":"Unknown"}""");

            Assert.Equal(2, PartnerRoleBackfillCommand.Run(connectionString, apply: false, legacyPath));
            Assert.Equal(2, PartnerRoleBackfillCommand.Run(connectionString, apply: true, legacyPath));
            Assert.Null(await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[0]));
        }
        finally
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            await DeletePartners(connectionString, ids);
        }
    }

    [PostgresFact]
    public async Task PartnerRoleBackfill_ImportsKnownRoles_AndDefaultsMissingEntryToBoth()
    {
        var connectionString = RequiredConnection();
        var ids = await InsertPartners(connectionString, 4);
        var legacyPath = Path.Combine(Path.GetTempPath(), $"flowstock-partner-roles-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new Dictionary<long, string>
            {
                [ids[0]] = "Supplier",
                [ids[1]] = "Client",
                [ids[2]] = "Both"
            }));

            Assert.Equal(0, PartnerRoleBackfillCommand.Run(connectionString, apply: false, legacyPath));
            Assert.Equal(4, await ScalarLong(
                connectionString,
                "SELECT COUNT(*) FROM partners WHERE id = ANY(@ids) AND partner_role IS NULL;",
                ids));

            Assert.Equal(0, PartnerRoleBackfillCommand.Run(connectionString, apply: true, legacyPath));
            Assert.Equal("SUPPLIER", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[0]));
            Assert.Equal("CLIENT", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[1]));
            Assert.Equal("BOTH", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[2]));
            Assert.Equal("BOTH", await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[3]));
        }
        finally
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            await DeletePartners(connectionString, ids);
        }
    }

    [PostgresFact]
    public async Task PartnerRoleBackfill_ConcurrentNullPartnerInsert_FailsClosedAndRollsBack()
    {
        var connectionString = RequiredConnection();
        var ids = await InsertPartners(connectionString, 1);
        long[] concurrentIds = [];
        var legacyPath = Path.Combine(Path.GetTempPath(), $"flowstock-partner-roles-{Guid.NewGuid():N}.json");
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        var lockTransactionCompleted = false;
        try
        {
            await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new Dictionary<long, string>
            {
                [ids[0]] = "Client"
            }));
            var lockCommand = new NpgsqlCommand(
                "SELECT id FROM partners WHERE id = @id FOR UPDATE;",
                lockConnection,
                lockTransaction);
            lockCommand.Parameters.AddWithValue("id", ids[0]);
            await lockCommand.ExecuteScalarAsync();

            var apply = Task.Run(() => PartnerRoleBackfillCommand.Run(connectionString, apply: true, legacyPath));
            await WaitForPartnerRoleBackfillUpdateLock(connectionString);
            concurrentIds = await InsertPartners(connectionString, 1);
            await lockTransaction.CommitAsync();
            lockTransactionCompleted = true;

            Assert.Equal(3, await apply);
            Assert.Null(await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", ids[0]));
            Assert.Null(await ScalarString(connectionString, "SELECT partner_role FROM partners WHERE id = @id;", concurrentIds[0]));
        }
        finally
        {
            if (!lockTransactionCompleted)
            {
                await lockTransaction.RollbackAsync();
            }
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            await DeletePartners(connectionString, ids.Concat(concurrentIds).ToArray());
        }
    }

    [PostgresFact]
    public async Task CustomerPriceService_UsesLegacyRoleForNullPartnerRole_AndDefaultsMissingEntryToBoth()
    {
        var connectionString = RequiredConnection();
        var ids = await InsertPartners(connectionString, 2);
        var legacyPath = Path.Combine(Path.GetTempPath(), $"flowstock-partner-roles-{Guid.NewGuid():N}.json");
        var store = new PostgresDataStore(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var itemId = new CatalogService(store).CreateItem(
            name: $"Товар {suffix}",
            barcode: $"LEGACY-PARTNER-ROLE-{suffix}",
            gtin: null,
            baseUom: "шт",
            brand: null,
            volume: null,
            shelfLifeMonths: null,
            taraId: null,
            isMarked: false);
        try
        {
            await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new Dictionary<long, string>
            {
                [ids[0]] = "Supplier"
            }));
            var service = new PartnerItemSalePriceService(
                store,
                new PartnerRoleResolver(store, legacyPath));

            var supplierError = Assert.Throws<CommercialTermsException>(() =>
                service.Create(ids[0], itemId, 100m, isActive: false));
            Assert.Equal("PARTNER_IS_SUPPLIER", supplierError.ErrorCode);

            var defaultBothPriceId = service.Create(ids[1], itemId, 100m, isActive: false);
            Assert.Equal(ids[1], store.GetPartnerItemSalePrice(defaultBothPriceId)?.PartnerId);
            service.Delete(defaultBothPriceId);
        }
        finally
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            store.DeleteItem(itemId);
            await DeletePartners(connectionString, ids);
        }
    }

    [PostgresFact]
    public async Task IdentifierMigration_FailsFastThenCreatesIndexesAndRejectsConcurrentDuplicates()
    {
        var connectionString = RequiredConnection();
        var schema = "catalog_identifier_" + Guid.NewGuid().ToString("N");
        var migration = ReadRepoFile("deploy", "postgres", "migrations", "V0034__item_identifier_case_insensitive_uniqueness.sql");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await new NpgsqlCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.items(id BIGSERIAL PRIMARY KEY, barcode TEXT, gtin TEXT); INSERT INTO {schema}.items(barcode) VALUES('Dup'),(' dup ');", connection).ExecuteNonQueryAsync();
            await using (var failed = await connection.BeginTransactionAsync())
            {
                await new NpgsqlCommand($"SET LOCAL search_path TO {schema};", connection, failed).ExecuteNonQueryAsync();
                var error = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(migration, connection, failed).ExecuteNonQueryAsync());
                Assert.Contains("duplicate item barcode", error.MessageText, StringComparison.OrdinalIgnoreCase);
                await failed.RollbackAsync();
            }

            await new NpgsqlCommand($"DELETE FROM {schema}.items;", connection).ExecuteNonQueryAsync();
            await using (var applied = await connection.BeginTransactionAsync())
            {
                await new NpgsqlCommand($"SET LOCAL search_path TO {schema};", connection, applied).ExecuteNonQueryAsync();
                await new NpgsqlCommand(migration, connection, applied).ExecuteNonQueryAsync();
                await applied.CommitAsync();
            }

            var first = InsertIdentifier(connectionString, schema, "Sku-One", "04600000000001");
            var second = InsertIdentifier(connectionString, schema, " sku-one ", " 04600000000001 ");
            var outcomes = await Task.WhenAll(first, second);
            Assert.Single(outcomes.Where(error => error == null));
            Assert.Single(outcomes.Where(error => error?.SqlState == PostgresErrorCodes.UniqueViolation));

            var gtinError = await Assert.ThrowsAsync<PostgresException>(() =>
                new NpgsqlCommand($"INSERT INTO {schema}.items(barcode, gtin) VALUES('SKU-TWO', ' 04600000000001 ');", connection).ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, gtinError.SqlState);
        }
        finally
        {
            await new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE;", connection).ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task ItemReadBoundary_HidesAdministrativeVariantFromOperatorDirectHttp()
    {
        var connectionString = RequiredConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var ids = await InsertReadBoundaryFixtures(connectionString, suffix);
        try
        {
            await using var operatorHost = await StartItemHost(connectionString, new PcWebIdentity(1, "PC", "operator", "PC", PcAccessRole.Operator));
            var operational = await operatorHost.GetTestClient().GetAsync("/api/items");
            operational.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await operational.Content.ReadAsStringAsync()))
            {
                var returnedIds = json.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToArray();
                Assert.Contains(ids.VisibleActiveItemId, returnedIds);
                Assert.DoesNotContain(ids.HiddenActiveItemId, returnedIds);
                Assert.DoesNotContain(ids.VisibleInactiveItemId, returnedIds);
            }

            var forbidden = await operatorHost.GetTestClient().GetAsync("/api/items?include_inactive=1");
            Assert.Equal(StatusCodes.Status403Forbidden, (int)forbidden.StatusCode);

            await using var anonymousHost = await StartItemHost(connectionString, null);
            var unauthorized = await anonymousHost.GetTestClient().GetAsync("/api/items?include_inactive=1");
            Assert.Equal(StatusCodes.Status401Unauthorized, (int)unauthorized.StatusCode);

            await using var adminHost = await StartItemHost(connectionString, new PcWebIdentity(2, "PC", "admin", "PC", PcAccessRole.Admin));
            var administrative = await adminHost.GetTestClient().GetAsync("/api/items?include_inactive=1");
            administrative.EnsureSuccessStatusCode();
            using var adminJson = JsonDocument.Parse(await administrative.Content.ReadAsStringAsync());
            var adminIds = adminJson.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToArray();
            Assert.Contains(ids.VisibleActiveItemId, adminIds);
            Assert.Contains(ids.HiddenActiveItemId, adminIds);
            Assert.Contains(ids.VisibleInactiveItemId, adminIds);
        }
        finally
        {
            await DeleteReadBoundaryFixtures(connectionString, ids);
        }
    }

    private static async Task<PostgresException?> InsertIdentifier(string connectionString, string schema, string barcode, string gtin)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"INSERT INTO {schema}.items(barcode, gtin) VALUES(@barcode, @gtin);", connection)
            {
                Parameters = { new("@barcode", barcode), new("@gtin", gtin) }
            }.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }

    private static async Task<WebApplication> StartItemHost(string connectionString, PcWebIdentity? identity)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WpfMachineAuthorization("test-machine-key-at-least-32-characters"));
        builder.Services.AddSingleton<IPcWebSessionResolver>(new FixedResolver(identity));
        builder.Services.AddSingleton<CatalogAuthorization>();
        builder.Services.AddSingleton<IDataStore>(new PostgresDataStore(connectionString));
        builder.Services.AddSingleton<CatalogService>();
        var app = builder.Build();
        ItemCatalogEndpoints.Map(app, connectionString);
        await app.StartAsync();
        return app;
    }

    private static async Task<ReadBoundaryFixture> InsertReadBoundaryFixtures(string connectionString, string suffix)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var visibleType = await InsertType(connection, "Visible " + suffix, "VIS-" + suffix, true);
        var hiddenType = await InsertType(connection, "Hidden " + suffix, "HID-" + suffix, false);
        var visibleActive = await InsertItem(connection, "Visible " + suffix, "VIS-ITEM-" + suffix, visibleType, true);
        var hiddenActive = await InsertItem(connection, "Hidden " + suffix, "HID-ITEM-" + suffix, hiddenType, true);
        var visibleInactive = await InsertItem(connection, "Inactive " + suffix, "INA-ITEM-" + suffix, visibleType, false);
        return new ReadBoundaryFixture(visibleType, hiddenType, visibleActive, hiddenActive, visibleInactive);
    }

    private static async Task<long> InsertType(NpgsqlConnection connection, string name, string code, bool visible)
    {
        var command = new NpgsqlCommand("INSERT INTO item_types(name, code, is_active, is_visible_in_product_catalog) VALUES(@name, @code, TRUE, @visible) RETURNING id;", connection);
        command.Parameters.AddWithValue("@name", name); command.Parameters.AddWithValue("@code", code); command.Parameters.AddWithValue("@visible", visible);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertItem(NpgsqlConnection connection, string name, string barcode, long typeId, bool active)
    {
        var command = new NpgsqlCommand("INSERT INTO items(name, barcode, base_uom, item_type_id, is_active) VALUES(@name, @barcode, 'шт', @type_id, @active) RETURNING id;", connection);
        command.Parameters.AddWithValue("@name", name); command.Parameters.AddWithValue("@barcode", barcode); command.Parameters.AddWithValue("@type_id", typeId); command.Parameters.AddWithValue("@active", active);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task DeleteReadBoundaryFixtures(string connectionString, ReadBoundaryFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("DELETE FROM items WHERE id = ANY(@item_ids); DELETE FROM item_types WHERE id = ANY(@type_ids);", connection);
        command.Parameters.AddWithValue("@item_ids", new[] { fixture.VisibleActiveItemId, fixture.HiddenActiveItemId, fixture.VisibleInactiveItemId });
        command.Parameters.AddWithValue("@type_ids", new[] { fixture.VisibleTypeId, fixture.HiddenTypeId });
        await command.ExecuteNonQueryAsync();
    }

    private static PcWebIdentity? Resolve(string connectionString, string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{PcWebSessionStore.CookieName}={token}";
        return new PcWebSessionStore(connectionString).Resolve(context.Request);
    }

    private static async Task<long> CreateAccount(string connectionString, string login, string password, string role)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var derive = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(@"
INSERT INTO tsd_devices(device_id, login, password_salt, password_hash, password_iterations, platform, is_active, access_role, created_at)
VALUES(@device, @login, @salt, @hash, 100000, 'PC', TRUE, @role, @created) RETURNING id;", connection);
        command.Parameters.AddWithValue("@device", "PC-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@login", login);
        command.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("@hash", Convert.ToBase64String(derive.GetBytes(32)));
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("s"));
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long[]> InsertPartners(string connectionString, int count)
    {
        var result = new List<long>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var i = 0; i < count; i++)
        {
            var command = new NpgsqlCommand("INSERT INTO partners(name, code, created_at, partner_role) VALUES(@name, NULL, @created, NULL) RETURNING id;", connection);
            command.Parameters.AddWithValue("@name", "Backfill " + Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("s"));
            result.Add((long)(await command.ExecuteScalarAsync() ?? 0L));
        }
        return result.ToArray();
    }

    private static async Task Execute(string connectionString, string sql, long id)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLong(string connectionString, string sql, object value)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue(sql.Contains("@ids", StringComparison.Ordinal) ? "@ids" : "@id", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string?> ScalarString(string connectionString, string sql, long id)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("@id", id);
        var value = await command.ExecuteScalarAsync(); return value == null || value is DBNull ? null : Convert.ToString(value);
    }

    private static async Task DeleteAccount(string connectionString, long id)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("DELETE FROM pc_web_sessions WHERE account_id = @id; DELETE FROM tsd_devices WHERE id = @id;", connection); command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync();
    }

    private static async Task DeletePartners(string connectionString, long[] ids)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var command = new NpgsqlCommand("DELETE FROM partners WHERE id = ANY(@ids);", connection); command.Parameters.AddWithValue("@ids", ids); await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForPartnerRoleBackfillUpdateLock(string connectionString)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var command = new NpgsqlCommand(@"
SELECT EXISTS (
    SELECT 1
    FROM pg_stat_activity
    WHERE pid <> pg_backend_pid()
      AND datname = current_database()
      AND wait_event_type = 'Lock'
      AND query LIKE '%UPDATE partners%partner_role%'
);", connection);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync()))
            {
                return;
            }
            await Task.Delay(20);
        }

        throw new TimeoutException("Partner-role backfill did not reach the blocked UPDATE.");
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
        throw new FileNotFoundException("Не удалось найти migration.", Path.Combine(parts));
    }

    private sealed class FixedResolver(PcWebIdentity? identity) : IPcWebSessionResolver
    {
        public PcWebIdentity? Resolve(HttpRequest request) => identity;
    }

    private sealed record ReadBoundaryFixture(
        long VisibleTypeId,
        long HiddenTypeId,
        long VisibleActiveItemId,
        long HiddenActiveItemId,
        long VisibleInactiveItemId);
}

public static class CatalogCutoverPostgresTestCollection
{
    public const string Name = "Catalog cutover PostgreSQL maintenance";
}

[CollectionDefinition(CatalogCutoverPostgresTestCollection.Name, DisableParallelization = true)]
public sealed class CatalogCutoverPostgresTestCollectionDefinition;
