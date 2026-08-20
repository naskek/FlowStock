using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace FlowStock.Server;

public static class PcAccessRole
{
    public const string Operator = "OPERATOR";
    public const string Admin = "ADMIN";
    public const string ManagePendingRequests = "ManagePendingRequests";
    public const string ManageCatalog = "ManageCatalog";
    public const string InvalidAccessRole = "INVALID_ACCESS_ROLE";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Admin, StringComparison.OrdinalIgnoreCase) ? Admin : Operator;

    public static IResult? ValidateInput(string? value, out string accessRole)
    {
        accessRole = Operator;
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), Operator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(value.Trim(), Admin, StringComparison.OrdinalIgnoreCase))
        {
            accessRole = Admin;
            return null;
        }

        return Results.BadRequest(new ApiResult(false, InvalidAccessRole));
    }
}

public sealed record PcWebIdentity(long AccountId, string DeviceId, string Login, string Platform, string AccessRole)
{
    public bool CanManagePendingRequests => string.Equals(AccessRole, PcAccessRole.Admin, StringComparison.Ordinal);
    public bool CanManageCatalog => string.Equals(AccessRole, PcAccessRole.Admin, StringComparison.Ordinal);
}

public interface IPcWebSessionResolver
{
    PcWebIdentity? Resolve(HttpRequest request);
}

public sealed class PcWebSessionStore(string connectionString) : IPcWebSessionResolver
{
    public const string CookieName = "flowstock_pc_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public PcWebLoginResult Login(string login, string password, DateTimeOffset now)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, device_id, login, password_salt, password_hash, password_iterations,
       is_active, platform, access_role
FROM tsd_devices
WHERE UPPER(login) = UPPER(@login)
LIMIT 1;";
        AddParam(command, "@login", login.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return PcWebLoginResult.Invalid("INVALID_CREDENTIALS");
        }

        var accountId = reader.GetInt64(0);
        var deviceId = reader.GetString(1);
        var storedLogin = reader.GetString(2);
        var salt = reader.GetString(3);
        var hash = reader.GetString(4);
        var iterations = reader.GetInt32(5);
        var isActive = reader.GetBoolean(6);
        var platform = reader.IsDBNull(7) ? "TSD" : reader.GetString(7);
        var accessRole = reader.IsDBNull(8) ? PcAccessRole.Operator : PcAccessRole.Normalize(reader.GetString(8));
        reader.Close();

        if (!isActive || !IsPcPlatform(platform))
        {
            return PcWebLoginResult.Invalid("PC_ACCESS_DENIED");
        }

        if (!VerifyPassword(password, salt, hash, iterations))
        {
            return PcWebLoginResult.Invalid("INVALID_CREDENTIALS");
        }

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var expiresAt = now.Add(Lifetime);
        using var transaction = connection.BeginTransaction();
        using (var lockAccount = connection.CreateCommand())
        {
            lockAccount.Transaction = transaction;
            lockAccount.CommandText = @"
SELECT device_id, login, password_salt, password_hash, password_iterations,
       is_active, platform, access_role
FROM tsd_devices
WHERE id = @id
FOR UPDATE;";
            AddParam(lockAccount, "@id", accountId);
            using var current = lockAccount.ExecuteReader();
            if (!current.Read())
            {
                transaction.Rollback();
                return PcWebLoginResult.Invalid("INVALID_CREDENTIALS");
            }

            deviceId = current.GetString(0);
            storedLogin = current.GetString(1);
            salt = current.GetString(2);
            hash = current.GetString(3);
            iterations = current.GetInt32(4);
            isActive = current.GetBoolean(5);
            platform = current.IsDBNull(6) ? "TSD" : current.GetString(6);
            accessRole = current.IsDBNull(7)
                ? PcAccessRole.Operator
                : PcAccessRole.Normalize(current.GetString(7));
        }

        if (!isActive || !IsPcPlatform(platform))
        {
            transaction.Rollback();
            return PcWebLoginResult.Invalid("PC_ACCESS_DENIED");
        }
        if (!VerifyPassword(password, salt, hash, iterations))
        {
            transaction.Rollback();
            return PcWebLoginResult.Invalid("INVALID_CREDENTIALS");
        }
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM pc_web_sessions WHERE expires_at < @cleanup_before;";
            AddParam(cleanup, "@cleanup_before", now.AddDays(-7).UtcDateTime);
            cleanup.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT INTO pc_web_sessions(account_id, token_hash, created_at, expires_at, revoked_at)
VALUES(@account_id, @token_hash, @created_at, @expires_at, NULL);";
            AddParam(insert, "@account_id", accountId);
            AddParam(insert, "@token_hash", HashToken(rawToken));
            AddParam(insert, "@created_at", now.UtcDateTime);
            AddParam(insert, "@expires_at", expiresAt.UtcDateTime);
            insert.ExecuteNonQuery();
        }

        if (string.Equals(accessRole, PcAccessRole.Admin, StringComparison.Ordinal))
        {
            using var revoke = connection.CreateCommand();
            revoke.Transaction = transaction;
            revoke.CommandText = @"
UPDATE pc_web_sessions
SET revoked_at = @revoked_at
WHERE account_id = @account_id
  AND token_hash <> @new_token_hash
  AND revoked_at IS NULL;";
            AddParam(revoke, "@revoked_at", now.UtcDateTime);
            AddParam(revoke, "@account_id", accountId);
            AddParam(revoke, "@new_token_hash", HashToken(rawToken));
            revoke.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE tsd_devices SET last_seen = @last_seen WHERE id = @id;";
            AddParam(update, "@last_seen", now.LocalDateTime.ToString("s", CultureInfo.InvariantCulture));
            AddParam(update, "@id", accountId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return new PcWebLoginResult(
            true,
            null,
            rawToken,
            expiresAt,
            new PcWebIdentity(accountId, deviceId, storedLogin, platform.Trim().ToUpperInvariant(), accessRole));
    }

    public PcWebIdentity? Resolve(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var rawToken) || string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT d.id, d.device_id, d.login, d.platform, d.access_role
FROM pc_web_sessions s
JOIN tsd_devices d ON d.id = s.account_id
WHERE s.token_hash = @token_hash
  AND s.revoked_at IS NULL
  AND s.expires_at > @now
  AND d.is_active = TRUE
  AND UPPER(COALESCE(d.platform, 'TSD')) IN ('PC', 'BOTH')
LIMIT 1;";
        AddParam(command, "@token_hash", HashToken(rawToken));
        AddParam(command, "@now", DateTime.UtcNow);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PcWebIdentity(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? "PC" : reader.GetString(3).Trim().ToUpperInvariant(),
            PcAccessRole.Normalize(reader.IsDBNull(4) ? null : reader.GetString(4)));
    }

    public void Revoke(HttpRequest request, DateTimeOffset now)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var rawToken) || string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE pc_web_sessions
SET revoked_at = @revoked_at
WHERE token_hash = @token_hash
  AND revoked_at IS NULL;";
        AddParam(command, "@revoked_at", now.UtcDateTime);
        AddParam(command, "@token_hash", HashToken(rawToken));
        command.ExecuteNonQuery();
    }

    public static void RevokeForAdminPromotion(
        DbConnection connection,
        DbTransaction transaction,
        long accountId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
UPDATE pc_web_sessions
SET revoked_at = @revoked_at
WHERE account_id = @account_id
  AND revoked_at IS NULL;";
        AddParam(command, "@revoked_at", now.UtcDateTime);
        AddParam(command, "@account_id", accountId);
        command.ExecuteNonQuery();
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static bool IsPcPlatform(string? value) =>
        string.Equals(value?.Trim(), "PC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "BOTH", StringComparison.OrdinalIgnoreCase);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool VerifyPassword(string password, string saltBase64, string hashBase64, int iterations)
    {
        if (string.IsNullOrWhiteSpace(password) || iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            using var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(derive.GetBytes(expected.Length), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void AddParam(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public sealed record PcWebLoginResult(
    bool IsSuccess,
    string? Error,
    string? Token,
    DateTimeOffset? ExpiresAt,
    PcWebIdentity? Identity)
{
    public static PcWebLoginResult Invalid(string error) => new(false, error, null, null, null);
}

public static class PcWebSessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/pc/login", async (HttpRequest request, HttpResponse response, PcWebSessionStore sessions, IDataStore store) =>
        {
            TsdLoginRequest? loginRequest;
            try
            {
                loginRequest = await request.ReadFromJsonAsync<TsdLoginRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_JSON"));
            }
            if (loginRequest == null
                || string.IsNullOrWhiteSpace(loginRequest.Login)
                || string.IsNullOrWhiteSpace(loginRequest.Password))
            {
                return Results.BadRequest(new ApiResult(false, "MISSING_CREDENTIALS"));
            }

            var result = sessions.Login(loginRequest.Login, loginRequest.Password, DateTimeOffset.UtcNow);
            if (!result.IsSuccess || result.Identity == null || result.Token == null || !result.ExpiresAt.HasValue)
            {
                return Results.Json(new ApiResult(false, result.Error), statusCode: StatusCodes.Status401Unauthorized);
            }

            response.Cookies.Append(PcWebSessionStore.CookieName, result.Token, BuildCookie(result.ExpiresAt.Value));
            return Results.Ok(BuildSessionEnvelope(result.Identity, result.ExpiresAt.Value, store));
        });

        app.MapGet("/api/pc/session", (HttpRequest request, IPcWebSessionResolver sessions, IDataStore store) =>
        {
            var identity = sessions.Resolve(request);
            return identity == null
                ? Results.Json(new ApiResult(false, "INVALID_SESSION"), statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(BuildSessionEnvelope(identity, (DateTimeOffset?)null, store));
        });

        app.MapPost("/api/pc/logout", (HttpRequest request, HttpResponse response, PcWebSessionStore sessions) =>
        {
            sessions.Revoke(request, DateTimeOffset.UtcNow);
            response.Cookies.Delete(PcWebSessionStore.CookieName, BuildCookie(DateTimeOffset.UtcNow.AddDays(-1)));
            return Results.Ok(new ApiResult(true));
        });
    }

    private static CookieOptions BuildCookie(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
        MaxAge = expires > DateTimeOffset.UtcNow ? expires - DateTimeOffset.UtcNow : TimeSpan.Zero
    };

    private static object BuildSessionEnvelope(PcWebIdentity identity, DateTimeOffset? expiresAt, IDataStore store) => new
    {
        ok = true,
        account = new
        {
            device_id = identity.DeviceId,
            login = identity.Login,
            platform = identity.Platform,
            access_role = identity.AccessRole
        },
        capabilities = identity.CanManagePendingRequests
            ? new[] { PcAccessRole.ManagePendingRequests, PcAccessRole.ManageCatalog }
            : Array.Empty<string>(),
        expires_at = expiresAt?.ToString("O", CultureInfo.InvariantCulture),
        blocks = ClientBlockCatalog.MergeWithDefaults(store.GetClientBlockSettings())
    };
}
