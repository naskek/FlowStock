using System.Net.Http;

namespace FlowStock.App;

internal static class WpfTrustedRequestHeaders
{
    internal const string AdminKeyHeader = "X-FlowStock-WPF-Admin-Key";
    internal const string AuditActorHeader = "X-FlowStock-WPF-Audit-Actor";

    public static void Add(HttpClient client, string? adminApiKey)
    {
        if (string.IsNullOrWhiteSpace(adminApiKey))
        {
            return;
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation(AdminKeyHeader, adminApiKey.Trim());
        client.DefaultRequestHeaders.TryAddWithoutValidation(AuditActorHeader, Environment.UserName);
    }

    public static string? ReadAdminApiKey(ServerSettings settings)
    {
        var value = Environment.GetEnvironmentVariable("FLOWSTOCK_WPF_ADMIN_API_KEY");
        return string.IsNullOrWhiteSpace(value) ? settings.WpfAdminApiKey?.Trim() : value.Trim();
    }
}
