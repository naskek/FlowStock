using System.Security.Cryptography;
using FlowStock.Server.Tests.Tsd;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace FlowStock.Server.Tests.Pc;

public sealed class PcWebSessionPostgresTests
{
    [PostgresFact]
    public async Task ExistingSession_UsesPersistedTokenAndCurrentAccountAuthorizationState()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var suffix = Guid.NewGuid().ToString("N");
        var login = $"pc-{suffix}";
        var deviceId = $"PC-{suffix}";
        const string password = "test-password";
        var accountId = await CreateAccountAsync(connectionString, deviceId, login, password);

        try
        {
            var loginAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var loginResult = new PcWebSessionStore(connectionString).Login(login, password, loginAt);
            Assert.True(loginResult.IsSuccess);
            Assert.Equal(loginAt.Add(PcWebSessionStore.Lifetime), loginResult.ExpiresAt);

            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = $"{PcWebSessionStore.CookieName}={loginResult.Token}";
            var restartedSessions = new PcWebSessionStore(connectionString);
            var identity = Assert.IsType<PcWebIdentity>(restartedSessions.Resolve(context.Request));
            Assert.True(identity.CanManagePendingRequests);

            await SetRoleAsync(connectionString, accountId, PcAccessRole.Operator);
            identity = Assert.IsType<PcWebIdentity>(restartedSessions.Resolve(context.Request));
            Assert.False(identity.CanManagePendingRequests);

            await SetRoleAsync(connectionString, accountId, PcAccessRole.Admin);
            Assert.True(Assert.IsType<PcWebIdentity>(restartedSessions.Resolve(context.Request)).CanManagePendingRequests);

            await SetActiveAsync(connectionString, accountId, false);
            Assert.Null(restartedSessions.Resolve(context.Request));
            await SetActiveAsync(connectionString, accountId, true);
            Assert.NotNull(restartedSessions.Resolve(context.Request));

            await SetPlatformAsync(connectionString, accountId, "TSD");
            Assert.Null(restartedSessions.Resolve(context.Request));
            await SetPlatformAsync(connectionString, accountId, "BOTH");
            Assert.NotNull(restartedSessions.Resolve(context.Request));

            new PcWebSessionStore(connectionString).Revoke(context.Request, loginAt.AddMinutes(2));
            Assert.Null(restartedSessions.Resolve(context.Request));
        }
        finally
        {
            await DeleteAccountAsync(connectionString, accountId);
        }
    }

    [PostgresFact]
    public async Task Session_UsesAbsoluteTwelveHourExpiryStoredInPostgres()
    {
        var connectionString = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
        var suffix = Guid.NewGuid().ToString("N");
        var login = $"pc-expiry-{suffix}";
        var accountId = await CreateAccountAsync(connectionString, $"PC-EXPIRY-{suffix}", login, "test-password");

        try
        {
            var loginAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var loginResult = new PcWebSessionStore(connectionString).Login(login, "test-password", loginAt);
            Assert.True(loginResult.IsSuccess);
            Assert.Equal(loginAt.AddHours(12), loginResult.ExpiresAt);

            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = $"{PcWebSessionStore.CookieName}={loginResult.Token}";
            Assert.NotNull(new PcWebSessionStore(connectionString).Resolve(context.Request));

            await ExpireSessionsAsync(connectionString, accountId, DateTimeOffset.UtcNow.AddMinutes(-1));

            Assert.Null(new PcWebSessionStore(connectionString).Resolve(context.Request));
        }
        finally
        {
            await DeleteAccountAsync(connectionString, accountId);
        }
    }

    private static async Task<long> CreateAccountAsync(
        string connectionString,
        string deviceId,
        string login,
        string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var derive = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var hash = derive.GetBytes(32);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
INSERT INTO tsd_devices(
    device_id, login, password_salt, password_hash, password_iterations,
    platform, is_active, access_role, created_at)
VALUES(@device_id, @login, @salt, @hash, 100000, 'PC', TRUE, 'ADMIN', @created_at)
RETURNING id;", connection);
        command.Parameters.AddWithValue("@device_id", deviceId);
        command.Parameters.AddWithValue("@login", login);
        command.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        command.Parameters.AddWithValue("@created_at", DateTime.UtcNow.ToString("s"));
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task SetRoleAsync(string connectionString, long accountId, string role)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE tsd_devices SET access_role = @role WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@id", accountId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetActiveAsync(string connectionString, long accountId, bool isActive)
    {
        await ExecuteAccountUpdateAsync(
            connectionString,
            "UPDATE tsd_devices SET is_active = @value WHERE id = @id;",
            accountId,
            isActive);
    }

    private static async Task SetPlatformAsync(string connectionString, long accountId, string platform)
    {
        await ExecuteAccountUpdateAsync(
            connectionString,
            "UPDATE tsd_devices SET platform = @value WHERE id = @id;",
            accountId,
            platform);
    }

    private static async Task ExpireSessionsAsync(
        string connectionString,
        long accountId,
        DateTimeOffset expiresAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE pc_web_sessions SET expires_at = @expires_at WHERE account_id = @account_id;",
            connection);
        command.Parameters.AddWithValue("@expires_at", expiresAt.UtcDateTime);
        command.Parameters.AddWithValue("@account_id", accountId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task ExecuteAccountUpdateAsync(
        string connectionString,
        string sql,
        long accountId,
        object value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@id", accountId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task DeleteAccountAsync(string connectionString, long accountId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM tsd_devices WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", accountId);
        await command.ExecuteNonQueryAsync();
    }
}
