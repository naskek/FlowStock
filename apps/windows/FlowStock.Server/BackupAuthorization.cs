using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace FlowStock.Server;

public static class BackupPermission
{
    public const string Read = "backup.read";
    public const string Create = "backup.create";
    public const string Configure = "backup.configure";
    public const string Probe = "backup.probe";
    public const string Replicate = "backup.replicate";

    public static bool IsValid(string? value) => value is Read or Create or Configure or Probe or Replicate;
}

public enum BackupAccessOutcome { Allowed, InvalidSession, Forbidden, NotFound, Unavailable }

// Только server-derived stable ID. Не включать login/device/claims/paths в audit.
public sealed record BackupAccessDecision
{
    internal BackupAccessDecision(long? accountId, BackupAccessOutcome outcome)
        => (AccountId, Outcome) = (accountId, outcome);
    public long? AccountId { get; }
    public BackupAccessOutcome Outcome { get; }
    public bool IsAllowed => Outcome == BackupAccessOutcome.Allowed;
}

/// <summary>
/// Backup-only gate. Каждый вызов повторно читает personal session и grants одним statement.
/// Resolve/Refresh legacy PC identity, ADMIN, machine key и claimed actor не являются authority.
/// </summary>
public sealed class BackupAuthorization(string connectionString, ILogger<BackupAuthorization> logger)
{
    public BackupAccessDecision Require(HttpRequest request, string permission)
        => Evaluate(request, permission, false, null);

    // ownerAccountId берётся только из server-owned durable mapping, не из body/header.
    // Missing и foreign request/job имеют один outcome, без раскрытия владельца.
    public BackupAccessDecision RequireOwned(HttpRequest request, string permission, long? ownerAccountId)
        => Evaluate(request, permission, true, ownerAccountId);

    private BackupAccessDecision Evaluate(HttpRequest request, string permission, bool owned, long? owner)
    {
        if (!BackupPermission.IsValid(permission))
            throw new ArgumentException("Unknown backup permission.", nameof(permission));

        long? actor = null;
        var outcome = BackupAccessOutcome.InvalidSession;
        if (request.Cookies.TryGetValue(PcWebSessionStore.CookieName, out var token)
            && token.Length is > 0 and <= 128)
        {
            try
            {
                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                using var command = new NpgsqlCommand(@"
SELECT d.id, EXISTS(SELECT 1 FROM backup_grants g
                   WHERE g.account_id = d.id AND g.permission = @permission)
FROM pc_web_sessions s JOIN tsd_devices d ON d.id = s.account_id
WHERE s.token_hash = @token_hash AND s.revoked_at IS NULL AND s.expires_at > CURRENT_TIMESTAMP
  AND d.is_active AND UPPER(COALESCE(d.platform, 'TSD')) IN ('PC','BOTH')
  AND s.backup_auth_version = d.backup_auth_version;", connection);
                command.Parameters.AddWithValue("token_hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
                command.Parameters.AddWithValue("permission", permission);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    actor = reader.GetInt64(0);
                    outcome = !reader.GetBoolean(1) ? BackupAccessOutcome.Forbidden
                        : owned && owner != actor ? BackupAccessOutcome.NotFound
                        : BackupAccessOutcome.Allowed;
                }
            }
            catch (NpgsqlException)
            {
                // Никогда не логировать raw exception/connection string/SQL/credential.
                outcome = BackupAccessOutcome.Unavailable;
            }
        }

        logger.LogInformation("Backup authorization actor={ActorId} permission={Permission} outcome={Outcome}",
            actor, permission, outcome);
        return new BackupAccessDecision(actor, outcome);
    }
}

public sealed record BackupCredentialReference(Guid Reference, Guid Revision);

/// <summary>
/// Операторский реестр разрешённых opaque refs, НЕ хранилище секретов.
/// Worker обязан отдельно проверить protected provider binding до использования.
/// Нет разрешения произвольного path, получения secret или implicit fallback.
/// </summary>
public sealed class BackupCredentialReferences(string connectionString)
{
    public BackupCredentialReference? Resolve(Guid reference, Guid expectedRevision)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = new NpgsqlCommand(@"
SELECT 1 FROM backup_credential_refs WHERE credential_ref = @reference AND revision = @revision
AND state = 'CONFIGURED' AND expires_at > CURRENT_TIMESTAMP;", connection);
            command.Parameters.AddWithValue("reference", reference);
            command.Parameters.AddWithValue("revision", expectedRevision);
            return command.ExecuteScalar() == null ? null : new(reference, expectedRevision);
        }
        catch (NpgsqlException) { return null; }
    }
}
