using System.Globalization;
using Npgsql;

namespace FlowStock.Server;

// Запускается только локальным доверенным оператором с доступом к DB credentials.
// Не регистрировать эти методы как HTTP handlers / WPF machine actions.
public static class BackupSecurityCommand
{
    public static bool TryRun(string[] args, string connectionString, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--backup-security") return false;
        try
        {
            if (args.Length == 4 && args[1] == "grants"
                && long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                && accountId > 0)
            {
                var grants = args[3] == "none" ? Array.Empty<string>() : args[3].Split(',');
                ReplaceGrants(connectionString, accountId, grants);
                Console.WriteLine("BACKUP_GRANTS_REPLACED_RELOGIN_REQUIRED");
            }
            else if (args.Length == 3 && args[1] == "reference-create" && TryHours(args[2], out var hours))
            {
                var reference = CreateReference(connectionString, TimeSpan.FromHours(hours));
                Console.WriteLine($"{reference.Reference:D} {reference.Revision:D}");
            }
            else if (args.Length == 6 && args[1] == "reference-update"
                && Guid.TryParseExact(args[2], "D", out var reference)
                && Guid.TryParseExact(args[3], "D", out var revision)
                && TryHours(args[5], out hours))
            {
                var changed = UpdateReference(connectionString, reference, revision, args[4], TimeSpan.FromHours(hours));
                Console.WriteLine($"{changed.Reference:D} {changed.Revision:D}");
            }
            else throw new ArgumentException();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NpgsqlException)
        {
            Console.Error.WriteLine("BACKUP_SECURITY_OPERATION_REJECTED");
            exitCode = 1;
        }
        return true;
    }

    public static void ReplaceGrants(string connectionString, long accountId, IReadOnlyCollection<string> permissions)
    {
        if (permissions.Any(p => !BackupPermission.IsValid(p)) || permissions.Distinct().Count() != permissions.Count)
            throw new ArgumentException("Invalid backup grants.");
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var account = new NpgsqlCommand(@"
UPDATE tsd_devices SET backup_auth_version = backup_auth_version + 1
WHERE id = @id AND ( @empty OR (is_active AND UPPER(platform) IN ('PC','BOTH')) )
RETURNING id;", connection, transaction))
        {
            account.Parameters.AddWithValue("id", accountId);
            account.Parameters.AddWithValue("empty", permissions.Count == 0);
            if (account.ExecuteScalar() == null) throw new InvalidOperationException("Account unavailable.");
        }
        using (var delete = new NpgsqlCommand("DELETE FROM backup_grants WHERE account_id = @id;", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", accountId);
            delete.ExecuteNonQuery();
        }
        foreach (var permission in permissions)
        {
            using var insert = new NpgsqlCommand("INSERT INTO backup_grants VALUES(@id, @permission);", connection, transaction);
            insert.Parameters.AddWithValue("id", accountId);
            insert.Parameters.AddWithValue("permission", permission);
            insert.ExecuteNonQuery();
        }
        using var audit = new NpgsqlCommand("INSERT INTO backup_security_events(account_id,action) VALUES(@id,'GRANTS_REPLACED');", connection, transaction);
        audit.Parameters.AddWithValue("id", accountId);
        audit.ExecuteNonQuery();
        transaction.Commit();
    }

    public static BackupCredentialReference CreateReference(string connectionString, TimeSpan lifetime)
        => ChangeReference(connectionString, Guid.NewGuid(), null, "MISSING", lifetime);

    public static BackupCredentialReference UpdateReference(string connectionString, Guid reference, Guid expectedRevision,
        string state, TimeSpan lifetime) => ChangeReference(connectionString, reference, expectedRevision, state, lifetime);

    private static BackupCredentialReference ChangeReference(string connectionString, Guid reference, Guid? expectedRevision,
        string state, TimeSpan lifetime)
    {
        if (state is not ("MISSING" or "CONFIGURED" or "REVOKED")
            || lifetime < TimeSpan.FromHours(1) || lifetime > TimeSpan.FromHours(8760)) throw new ArgumentException();
        var revision = Guid.NewGuid();
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = new NpgsqlCommand(expectedRevision == null ? @"
INSERT INTO backup_credential_refs(credential_ref,revision,state,expires_at)
VALUES(@reference,@revision,@state,@expires);" : @"
UPDATE backup_credential_refs SET revision=@revision,state=@state,expires_at=@expires
WHERE credential_ref=@reference AND revision=@expected;", connection, transaction);
        command.Parameters.AddWithValue("reference", reference);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("expires", DateTime.UtcNow.Add(lifetime));
        if (expectedRevision != null) command.Parameters.AddWithValue("expected", expectedRevision.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Reference unavailable.");
        using var audit = new NpgsqlCommand("INSERT INTO backup_security_events(action) VALUES('REFERENCE_CHANGED');", connection, transaction);
        audit.ExecuteNonQuery();
        transaction.Commit();
        return new(reference, revision);
    }

    private static bool TryHours(string value, out int hours)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out hours) && hours is >= 1 and <= 8760;
}
