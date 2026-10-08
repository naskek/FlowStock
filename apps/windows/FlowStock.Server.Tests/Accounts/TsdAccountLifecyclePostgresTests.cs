using System.Security.Cryptography;
using FlowStock.Server.Tests.Tsd;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowStock.Server.Tests.Accounts;

public sealed class TsdAccountLifecyclePostgresTests
{
    private const string MachineKey = "test-account-lifecycle-machine-key-not-a-secret";

    [PostgresFact]
    public async Task Account_delete_requires_machine_key_and_rejects_unknown_account()
    {
        var connectionString = TestConnection();
        await using var app = await StartHost(connectionString);
        using var client = app.GetTestClient();

        using (var without = await client.DeleteAsync("/api/admin/tsd-devices/99999999"))
            Assert.Equal(StatusCodes.Status401Unauthorized, (int)without.StatusCode);
        client.DefaultRequestHeaders.Add(WpfMachineAuthorization.KeyHeader, "wrong-machine-key");
        using (var invalid = await client.DeleteAsync("/api/admin/tsd-devices/99999999"))
            Assert.Equal(StatusCodes.Status401Unauthorized, (int)invalid.StatusCode);
        client.DefaultRequestHeaders.Remove(WpfMachineAuthorization.KeyHeader);
        client.DefaultRequestHeaders.Add(WpfMachineAuthorization.KeyHeader, MachineKey);

        using (var invalidId = await client.DeleteAsync("/api/admin/tsd-devices/0"))
            Assert.Equal(StatusCodes.Status400BadRequest, (int)invalidId.StatusCode);
        using var missing = await client.DeleteAsync("/api/admin/tsd-devices/99999999");
        Assert.Equal(StatusCodes.Status404NotFound, (int)missing.StatusCode);
    }

    [PostgresFact]
    public async Task Deletion_cascades_PC_sessions_but_preserves_operational_history()
    {
        var cs = TestConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = await CreateAccount(cs, "delete-admin-"+suffix, "PC", "ADMIN");
        var target = await CreateAccount(cs, "delete-operator-"+suffix, "BOTH", "OPERATOR");
        long requestId = 0;
        try
        {
            var sessions = new PcWebSessionStore(cs);
            var login = sessions.Login(target.Login, "fixture-pass", DateTimeOffset.UtcNow);
            Assert.True(login.IsSuccess);
            var http = new DefaultHttpContext();
            http.Request.Headers.Cookie = $"{PcWebSessionStore.CookieName}={login.Token}";
            Assert.NotNull(sessions.Resolve(http.Request));

            requestId = await AddHistoricalRequest(cs, target.DeviceId, target.Login);
            await using var app = await StartHost(cs);
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add(WpfMachineAuthorization.KeyHeader, MachineKey);
            using var response = await client.DeleteAsync($"/api/admin/tsd-devices/{target.Id}");
            Assert.Equal(StatusCodes.Status200OK, (int)response.StatusCode);

            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM tsd_devices WHERE id = @id;", target.Id));
            Assert.Equal(0, await Count(cs, "SELECT COUNT(*) FROM pc_web_sessions WHERE account_id = @id;", target.Id));
            Assert.Null(sessions.Resolve(http.Request));
            Assert.Equal(1, await Count(cs,
                "SELECT COUNT(*) FROM item_requests WHERE id = @id;", requestId));
            var actor = await GetActor(cs, requestId);
            Assert.Equal(target.DeviceId, actor.DeviceId);
            Assert.Equal(target.Login, actor.Login);

            using var again = await client.DeleteAsync($"/api/admin/tsd-devices/{target.Id}");
            Assert.Equal(StatusCodes.Status404NotFound, (int)again.StatusCode);
            // Internal account ID and external device ID remain historically distinct.
            Assert.NotEqual(owner.DeviceId, target.DeviceId);
        }
        finally
        {
            if (requestId != 0)
                await Execute(cs, "DELETE FROM item_requests WHERE id = @id;", requestId);
            await Execute(cs, "DELETE FROM tsd_devices WHERE id = @id;", target.Id);
            await Execute(cs, "DELETE FROM tsd_devices WHERE id = @id;", owner.Id);
        }
    }

    [PostgresFact]
    public async Task Last_active_PC_admin_cannot_be_deleted_without_replacement()
    {
        var cs = TestConnection();
        // CI runs on a fresh isolated database; do not alter unrelated accounts.
        Assert.Equal(0, await Count(cs, @"
SELECT COUNT(*) FROM tsd_devices
WHERE is_active AND access_role = 'ADMIN' AND platform IN ('PC','BOTH');"));
        var suffix = Guid.NewGuid().ToString("N");
        var first = await CreateAccount(cs, "first-admin-"+suffix, "BOTH", "ADMIN");
        var second = await CreateAccount(cs, "second-admin-"+suffix, "PC", "ADMIN");
        try
        {
            Assert.Equal(TsdAccountLifecycle.DeleteOutcome.Deleted,
                TsdAccountLifecycle.Delete(cs, first.Id));
            Assert.Equal(TsdAccountLifecycle.DeleteOutcome.LastActivePcAdmin,
                TsdAccountLifecycle.Delete(cs, second.Id));
            Assert.Equal(1, await Count(cs,
                "SELECT COUNT(*) FROM tsd_devices WHERE id = @id;", second.Id));
            Assert.True(TsdAccountLifecycle.IsActivePcAdmin(true, "PC", "ADMIN"));
            Assert.False(TsdAccountLifecycle.IsActivePcAdmin(false, "PC", "ADMIN"));
            Assert.False(TsdAccountLifecycle.IsActivePcAdmin(true, "TSD", "ADMIN"));
        }
        finally
        {
            await Execute(cs, "DELETE FROM tsd_devices WHERE id = @id;", first.Id);
            await Execute(cs, "DELETE FROM tsd_devices WHERE id = @id;", second.Id);
        }
    }

    private static async Task<WebApplication> StartHost(string cs)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WpfMachineAuthorization(MachineKey));
        var app = builder.Build();
        TsdAccountLifecycle.Map(app, cs);
        await app.StartAsync();
        return app;
    }

    private static string TestConnection() =>
        TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()
        ?? throw new InvalidOperationException("PostgreSQL integration database not configured.");

    private static async Task<(long Id, string Login, string DeviceId)> CreateAccount(
        string cs, string login, string platform, string role)
    {
        var deviceId = "ACC-TEST-" + Guid.NewGuid().ToString("N");
        var salt = RandomNumberGenerator.GetBytes(16);
        using var passwordHash = new Rfc2898DeriveBytes(
            "fixture-pass", salt, 100_000, HashAlgorithmName.SHA256);
        var hash = passwordHash.GetBytes(32);
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(@"
INSERT INTO tsd_devices(device_id, login, password_salt, password_hash,
                        password_iterations, platform, is_active, access_role, created_at)
VALUES (@device, @login, @salt, @hash, 100000, @platform, TRUE, @role, @now)
RETURNING id;", connection);
        insert.Parameters.AddWithValue("@device", deviceId);
        insert.Parameters.AddWithValue("@login", login);
        insert.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        insert.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        insert.Parameters.AddWithValue("@platform", platform);
        insert.Parameters.AddWithValue("@role", role);
        insert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("s"));
        return ((long)(await insert.ExecuteScalarAsync() ?? 0L), login, deviceId);
    }

    private static async Task<long> AddHistoricalRequest(string cs, string deviceId, string login)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(@"
INSERT INTO item_requests(barcode, comment, device_id, login, created_at)
VALUES ('fixture-code', 'historical audit fixture', @device, @login, @now)
RETURNING id;", connection);
        insert.Parameters.AddWithValue("@device", deviceId);
        insert.Parameters.AddWithValue("@login", login);
        insert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("s"));
        return (long)(await insert.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<(string DeviceId, string Login)> GetActor(string cs, long id)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            "SELECT device_id, login FROM item_requests WHERE id = @id;", connection);
        query.Parameters.AddWithValue("@id", id);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<long> Count(string cs, string sql, long? id = null)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue) command.Parameters.AddWithValue("@id", id.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task Execute(string cs, string sql, long id)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }
}
