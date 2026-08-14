using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public sealed class WpfMachineAuthorization
{
    public const string KeyHeader = "X-FlowStock-WPF-Admin-Key";
    public const string AuditActorHeader = "X-FlowStock-WPF-Audit-Actor";

    private readonly byte[]? _configuredKeyHash;

    public WpfMachineAuthorization(string? configuredKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey) && configuredKey.Trim().Length >= 32)
        {
            _configuredKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey.Trim()));
        }
    }

    public bool IsAuthorized(HttpRequest request)
    {
        if (_configuredKeyHash == null || !request.Headers.TryGetValue(KeyHeader, out var supplied))
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied.ToString()));
        return CryptographicOperations.FixedTimeEquals(_configuredKeyHash, suppliedHash);
    }

    public static string GetAuditActor(HttpRequest request)
    {
        var label = request.Headers[AuditActorHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return "WPF";
        }

        var normalized = new string(label.Where(ch => !char.IsControl(ch)).Take(128).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "WPF" : $"WPF:{normalized}";
    }
}
