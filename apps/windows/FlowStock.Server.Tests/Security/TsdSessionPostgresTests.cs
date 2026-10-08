using System.Security.Cryptography;
using System.Text;
using FlowStock.Server.Tests.Tsd;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace FlowStock.Server.Tests.Security;

public sealed class TsdSessionPostgresTests
{
    [PostgresFact]
    public async Task Session_is_unforgeable_and_respects_expiry_disable_platform_and_account_delete()
    {
        var cs = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var account = await CreateAccount(cs);
        try
        {
            var sessions = new TsdSessionStore(cs);
            var now = DateTimeOffset.UtcNow;
            Assert.Null(sessions.Issue(account.Id, account.Login, account.Salt, "forged", 100_000, now));

            var token = Assert.IsType<string>(sessions.Issue(account.Id, account.Login,
                account.Salt, account.Hash, 100_000, now));
            var req = WithToken(token);
            using (var lease = sessions.OpenLease(req, now))
                Assert.Equal(account.DeviceId, Assert.IsType<TsdSessionStore.SessionLease>(lease).DeviceId);

            Assert.Null(sessions.OpenLease(WithToken(new string('A', 64)), now));
            Assert.Null(sessions.OpenLease(req, now.AddHours(25)));

            await Update(cs, account.Id, "UPDATE tsd_devices SET is_active = FALSE WHERE id = @id");
            Assert.Null(sessions.OpenLease(req, now));
            await Update(cs, account.Id,
                "UPDATE tsd_devices SET is_active = TRUE, platform = 'PC' WHERE id = @id");
            Assert.Null(sessions.OpenLease(req, now));
            await Update(cs, account.Id, "UPDATE tsd_devices SET platform = 'TSD' WHERE id = @id");

            await Update(cs, account.Id, "DELETE FROM tsd_devices WHERE id = @id");
            Assert.Null(sessions.OpenLease(req, now));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM tsd_sessions WHERE account_id = @id", account.Id));
        }
        finally
        {
            await Update(cs, account.Id, "DELETE FROM tsd_devices WHERE id = @id");
        }
    }

    [PostgresFact]
    public async Task Foreign_device_id_cannot_override_session_actor()
    {
        var cs = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var account = await CreateAccount(cs);
        try
        {
            var sessions = new TsdSessionStore(cs);
            var token = Assert.IsType<string>(sessions.Issue(account.Id, account.Login,
                account.Salt, account.Hash, 100_000, DateTimeOffset.UtcNow));
            var bad = NewRequest(token, "{\"device_id\":\"ACC-FORGED\",\"hu_code\":\"X\"}");
            var called = false;
            await TsdSessionAuthorization.InvokeAsync(bad,
                () => { called = true; return Task.CompletedTask; }, sessions);
            Assert.Equal(StatusCodes.Status403Forbidden, bad.Response.StatusCode);
            Assert.False(called);

            var valid = NewRequest(token, "{\"device_id\":\"" + account.DeviceId + "\",\"hu_code\":\"X\"}");
            await TsdSessionAuthorization.InvokeAsync(valid,
                () =>
                {
                    Assert.Equal(account.DeviceId, TsdSessionStore.VerifiedDeviceId(valid));
                    called = true;
                    return Task.CompletedTask;
                }, sessions);
            Assert.True(called);
            Assert.Equal(StatusCodes.Status200OK, valid.Response.StatusCode);

            var withoutCookie = NewRequest(null, "{}");
            await TsdSessionAuthorization.InvokeAsync(withoutCookie,
                () => throw new Exception("Unauthorized mutation reached handler"), sessions);
            Assert.Equal(StatusCodes.Status401Unauthorized, withoutCookie.Response.StatusCode);
        }
        finally
        {
            await Update(cs, account.Id, "DELETE FROM tsd_devices WHERE id = @id");
        }
    }

    [PostgresFact]
    public async Task Account_delete_waits_until_authorized_mutation_lease_finishes()
    {
        var cs = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var account = await CreateAccount(cs);
        try
        {
            var sessions = new TsdSessionStore(cs);
            var now = DateTimeOffset.UtcNow;
            var token = Assert.IsType<string>(sessions.Issue(account.Id, account.Login,
                account.Salt, account.Hash, 100_000, now));
            var req = WithToken(token);
            var lease = Assert.IsType<TsdSessionStore.SessionLease>(sessions.OpenLease(req, now));
            try
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var deletion = Task.Run(async () =>
                {
                    started.SetResult();
                    await Update(cs, account.Id, "DELETE FROM tsd_devices WHERE id = @id");
                });
                await started.Task;
                await Task.Delay(150);
                Assert.False(deletion.IsCompleted);
                lease.Dispose();
                await deletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                lease.Dispose();
            }
            Assert.Null(sessions.OpenLease(req, now));
        }
        finally
        {
            await Update(cs, account.Id, "DELETE FROM tsd_devices WHERE id = @id");
        }
    }

    private static HttpRequest WithToken(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{TsdSessionStore.CookieName}={token}";
        return context.Request;
    }

    private static DefaultHttpContext NewRequest(string? token, string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/tsd/production/fill-pallet";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        if (token != null) context.Request.Headers.Cookie = $"{TsdSessionStore.CookieName}={token}";
        return context;
    }

    private static async Task<(long Id, string Login, string DeviceId, string Salt, string Hash)> CreateAccount(string cs)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var login = "tsd-session-" + suffix;
        var deviceId = "TSD-SESSION-" + suffix;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 100_000,
            HashAlgorithmName.SHA256, 32);
        var saltText = Convert.ToBase64String(salt);
        var hashText = Convert.ToBase64String(hash);
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
INSERT INTO tsd_devices(device_id, login, password_salt, password_hash, password_iterations,
 platform, is_active, access_role, created_at)
VALUES (@device, @login, @salt, @hash, 100000, 'TSD', TRUE, 'OPERATOR', @created)
RETURNING id;", connection);
        command.Parameters.AddWithValue("@device", deviceId);
        command.Parameters.AddWithValue("@login", login);
        command.Parameters.AddWithValue("@salt", saltText);
        command.Parameters.AddWithValue("@hash", hashText);
        command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("s"));
        var id = (long)(await command.ExecuteScalarAsync() ?? 0L);
        return (id, login, deviceId, saltText, hashText);
    }

    private static async Task Update(string cs, long id, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string cs, string sql, long id)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0);
    }
}
