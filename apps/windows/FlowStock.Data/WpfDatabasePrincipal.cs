using Npgsql;

namespace FlowStock.Data;

public static class WpfDatabasePrincipal
{
    private static readonly string Policy = ReadPolicy();

    public static void Validate(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var identity = connection.CreateCommand();
        identity.CommandText = "SELECT session_user = 'flowstock_wpf' AND current_user = session_user";
        if (identity.ExecuteScalar() is not true)
        {
            throw new InvalidOperationException("WPF требует ограниченное подключение flowstock_wpf. Обратитесь к администратору; privileged fallback запрещён.");
        }

        using var verify = connection.CreateCommand();
        verify.CommandText = Policy;
        verify.ExecuteNonQuery();
    }

    private static string ReadPolicy()
    {
        using var stream = typeof(WpfDatabasePrincipal).Assembly.GetManifestResourceStream("FlowStock.Data.verify_wpf_principal.sql")
            ?? throw new InvalidOperationException("WPF DB policy отсутствует.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
