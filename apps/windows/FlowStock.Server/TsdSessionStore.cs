using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace FlowStock.Server;

public sealed class TsdSessionStore(string connectionString)
{
    public const string CookieName = "flowstock_tsd_session";
    public const string VerifiedDeviceIdItem = "FlowStock.VerifiedTsdDeviceId";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    // Recheck the verified credentials and account under an exclusive row lock.
    // A concurrent password change, rename or disable must invalidate issuance.
    public string? Issue(long id, string login, string salt, string hash, int iterations, DateTimeOffset now)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = new NpgsqlCommand(@"
SELECT login, password_salt, password_hash, password_iterations, is_active, platform
FROM tsd_devices WHERE id = @id FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        string currentLogin, currentSalt, currentHash, platform;
        int currentIterations;
        bool active;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            currentLogin = reader.GetString(0);
            currentSalt = reader.GetString(1);
            currentHash = reader.GetString(2);
            currentIterations = reader.GetInt32(3);
            active = reader.GetBoolean(4);
            platform = reader.IsDBNull(5) ? "TSD" : reader.GetString(5);
        }
        if (!active || !AllowsTsd(platform)
            || !string.Equals(currentLogin, login, StringComparison.Ordinal)
            || !string.Equals(currentSalt, salt, StringComparison.Ordinal)
            || !string.Equals(currentHash, hash, StringComparison.Ordinal)
            || currentIterations != iterations) return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var insert = new NpgsqlCommand(@"
INSERT INTO tsd_sessions(token_hash, account_id, expires_at)
VALUES(@hash, @account_id, @expires_at);", connection, transaction);
        insert.Parameters.AddWithValue("@hash", HashToken(token));
        insert.Parameters.AddWithValue("@account_id", id);
        insert.Parameters.AddWithValue("@expires_at", now.Add(Lifetime).UtcDateTime);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return token;
    }

    // Row locks are held until the HTTP handler finishes. Account delete/demotion
    // cannot commit between authentication and the protected business mutation.
    public SessionLease? OpenLease(HttpRequest request, DateTimeOffset now)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var token)
            || token is null || token.Length != 64 || !token.All(Uri.IsHexDigit))
            return null;

        var connection = new NpgsqlConnection(connectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            connection.Open();
            transaction = connection.BeginTransaction();
            using var command = new NpgsqlCommand(@"
SELECT a.device_id
FROM tsd_sessions s
JOIN tsd_devices a ON a.id = s.account_id
WHERE s.token_hash = @hash AND s.revoked_at IS NULL
  AND s.expires_at > @now AND a.is_active = TRUE
  AND UPPER(COALESCE(a.platform, 'TSD')) IN ('TSD', 'BOTH')
FOR SHARE OF a;", connection, transaction);
            command.Parameters.AddWithValue("@hash", HashToken(token));
            command.Parameters.AddWithValue("@now", now.UtcDateTime);
            var deviceId = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }

            // Always acquire the account row before the session row.
            // Account DELETE locks account -> FK-cascade sessions in this order.
            // Recheck expiry/revocation under the session lock, before handing out a lease.
            using var session = new NpgsqlCommand(@"
SELECT 1 FROM tsd_sessions
WHERE token_hash = @hash AND revoked_at IS NULL AND expires_at > @now
FOR SHARE;", connection, transaction);
            session.Parameters.AddWithValue("@hash", HashToken(token));
            session.Parameters.AddWithValue("@now", now.UtcDateTime);
            if (session.ExecuteScalar() == null)
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }

            return new SessionLease(connection, transaction, deviceId);
        }
        catch
        {
            transaction?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    public static bool AllowsTsd(string? platform) =>
        string.Equals(platform?.Trim(), "TSD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform?.Trim(), "BOTH", StringComparison.OrdinalIgnoreCase);

    public static string VerifiedDeviceId(HttpContext context) =>
        context.Items.TryGetValue(VerifiedDeviceIdItem, out var identity) && identity is string deviceId
            ? deviceId : throw new InvalidOperationException("TSD identity was not validated.");

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public sealed class SessionLease(NpgsqlConnection connection, NpgsqlTransaction transaction, string deviceId)
        : IDisposable
    {
        public string DeviceId { get; } = deviceId;
        public void Dispose()
        {
            transaction.Dispose();
            connection.Dispose();
        }
    }
}
