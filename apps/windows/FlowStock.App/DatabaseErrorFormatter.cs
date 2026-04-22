using Npgsql;

namespace FlowStock.App;

internal static class DatabaseErrorFormatter
{
    public static string Format(Exception ex)
    {
        if (TryBuildSchemaMessage(ex, out var message))
        {
            return message;
        }

        return ex.Message;
    }

    public static bool IsSchemaIssue(Exception ex)
    {
        return TryBuildSchemaMessage(ex, out _);
    }

    private static bool TryBuildSchemaMessage(Exception ex, out string message)
    {
        for (var current = ex; current != null; current = current.InnerException!)
        {
            if (current is InvalidOperationException invalidOperation
                && IsSchemaStatusMessage(invalidOperation.Message))
            {
                message = BuildSchemaMessage();
                return true;
            }

            if (current is PostgresException postgres
                && string.Equals(postgres.SqlState, PostgresErrorCodes.UndefinedColumn, StringComparison.Ordinal)
                && ContainsPackSingleHu(postgres.MessageText ?? postgres.Message))
            {
                message = BuildSchemaMessage();
                return true;
            }
        }

        message = string.Empty;
        return false;
    }

    private static bool IsSchemaStatusMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Database schema is outdated", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Database schema is not initialized", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Database schema has no applied migrations", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Missing table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPackSingleHu(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("pack_single_hu", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSchemaMessage()
    {
        return "Схема БД FlowStock не инициализирована или не содержит все обязательные миграции." +
               Environment.NewLine +
               "Примените миграции к PostgreSQL и перезапустите WPF/сервер.";
    }
}
