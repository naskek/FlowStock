using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace FlowStock.Server;

/// <summary>
/// Protected account deletion. Historical actor strings remain in operational tables.
/// </summary>
public static class TsdAccountLifecycle
{
    public enum DeleteOutcome { Deleted, NotFound, LastActivePcAdmin }

    public static void Map(WebApplication app, string connectionString)
    {
        app.MapDelete("/api/admin/tsd-devices/{id:long}", (
            long id, HttpRequest request, WpfMachineAuthorization authorization) =>
        {
            if (!authorization.IsAuthorized(request))
            {
                return Results.Json(new ApiResult(false, "WPF_ADMIN_KEY_REQUIRED"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (id <= 0)
                return Results.BadRequest(new ApiResult(false, "INVALID_ACCOUNT_ID"));

            return Delete(connectionString, id) switch
            {
                DeleteOutcome.Deleted => Results.Ok(new ApiResult(true)),
                DeleteOutcome.NotFound => Results.NotFound(new ApiResult(false, "DEVICE_NOT_FOUND")),
                DeleteOutcome.LastActivePcAdmin => Results.Conflict(
                    new ApiResult(false, "LAST_ACTIVE_PC_ADMIN")),
                _ => throw new InvalidOperationException("Unexpected account delete result.")
            };
        });
    }

    /// <summary>
    /// Serialize deletion against account updates. PostgreSQL cascades PC Web sessions only.
    /// Operational history stores actor text and must not be modified.
    /// </summary>
    public static DeleteOutcome Delete(string connectionString, long id)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        LockAccountMutations(connection, transaction);

        bool isActive;
        string platform;
        string role;
        using (var lookup = new NpgsqlCommand(@"
SELECT is_active, platform, access_role
FROM tsd_devices
WHERE id = @id
FOR UPDATE;", connection, transaction))
        {
            lookup.Parameters.AddWithValue("@id", id);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read())
            {
                // Disposing the reader before the transaction rolls back is required.
                return DeleteOutcome.NotFound;
            }
            isActive = reader.GetBoolean(0);
            platform = reader.GetString(1);
            role = reader.GetString(2);
        }

        if (IsActivePcAdmin(isActive, platform, role)
            && !HasOtherActivePcAdmin(connection, transaction, id))
        {
            transaction.Rollback();
            return DeleteOutcome.LastActivePcAdmin;
        }

        using (var delete = new NpgsqlCommand(
            "DELETE FROM tsd_devices WHERE id = @id;", connection, transaction))
        {
            delete.Parameters.AddWithValue("@id", id);
            if (delete.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Account deletion did not affect exactly one row.");
        }

        // FK pc_web_sessions(account_id) ON DELETE CASCADE revokes PC sessions atomically.
        transaction.Commit();
        return DeleteOutcome.Deleted;
    }

    public static void LockAccountMutations(DbConnection connection, DbTransaction transaction)
    {
        // Acquire the table lock before row locks in both delete and profile updates.
        // SHARE ROW EXCLUSIVE conflicts with concurrent writers and prevents two
        // administrators from independently deleting the final pair of PC admins.
        using var lockCommand = connection.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText = "LOCK TABLE tsd_devices IN SHARE ROW EXCLUSIVE MODE;";
        lockCommand.ExecuteNonQuery();
    }

    public static bool IsActivePcAdmin(bool active, string? platform, string? role) =>
        active
        && string.Equals(role, PcAccessRole.Admin, StringComparison.OrdinalIgnoreCase)
        && (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, "BOTH", StringComparison.OrdinalIgnoreCase));

    public static bool HasOtherActivePcAdmin(
        DbConnection connection, DbTransaction transaction, long excludedId)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
SELECT 1
FROM tsd_devices
WHERE id <> @id
  AND is_active = TRUE
  AND access_role = 'ADMIN'
  AND UPPER(platform) IN ('PC', 'BOTH')
LIMIT 1;";
        var idParameter = check.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = excludedId;
        check.Parameters.Add(idParameter);
        return check.ExecuteScalar() != null;
    }
}
