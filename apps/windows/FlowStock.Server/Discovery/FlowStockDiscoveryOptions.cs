using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace FlowStock.Server.Discovery;

public sealed record FlowStockDiscoveryOptions(
    string InstanceName,
    string CanonicalHttpsBaseUrl,
    string ApplicationVersion)
{
    public const string Product = "FlowStock";
    public const int ProtocolVersion = 1;
    public const int UdpPort = 7155;
    public const int MaxUdpPacketBytes = 1024;
    public const int MaxInstanceNameLength = 96;
    public const int MaxApplicationVersionLength = 96;

    public static FlowStockDiscoveryOptions FromConfiguration(IConfiguration configuration, string applicationVersion)
    {
        var baseUrl = configuration["FLOWSTOCK_PUBLIC_BASE_URL"];
        var instanceName = configuration["FLOWSTOCK_INSTANCE_NAME"];

        var options = new FlowStockDiscoveryOptions(
            ValidateInstanceName(instanceName),
            ValidateCanonicalHttpsBaseUrl(
                baseUrl,
                configuration["FLOWSTOCK_TLS_SERVER_NAME"],
                configuration["FLOWSTOCK_TLS_SANS"]),
            ValidateApplicationVersion(applicationVersion));
        options.EnsureUdpResponseFits();
        return options;
    }

    public FlowStockDiscoveryResponse ToResponse() =>
        new(
            Product,
            ProtocolVersion,
            InstanceName,
            CanonicalHttpsBaseUrl,
            ApplicationVersion);

    private static string ValidateInstanceName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("FLOWSTOCK_INSTANCE_NAME must be set for FlowStock discovery.");
        }

        return name;
    }

    private static string ValidateApplicationVersion(string? value)
    {
        var version = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        if (version.Length > MaxApplicationVersionLength)
        {
            throw new InvalidOperationException("Application version is too long for FlowStock discovery.");
        }

        return version;
    }

    private static string ValidateCanonicalHttpsBaseUrl(
        string? value,
        string? tlsServerName,
        string? tlsSans)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("FLOWSTOCK_PUBLIC_BASE_URL must be set for FlowStock discovery.");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("FLOWSTOCK_PUBLIC_BASE_URL must be an absolute HTTPS root URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("FLOWSTOCK_PUBLIC_BASE_URL must not contain userinfo.");
        }

        if (uri.Port == 0 || uri.Port > 65535)
        {
            throw new InvalidOperationException("FLOWSTOCK_PUBLIC_BASE_URL port must be in range 1..65535.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("FLOWSTOCK_PUBLIC_BASE_URL must not contain query or fragment.");
        }

        var path = uri.AbsolutePath;
        if (!string.IsNullOrEmpty(path) && path != "/")
        {
            throw new InvalidOperationException("FLOWSTOCK_PUBLIC_BASE_URL must not contain a path.");
        }

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        var normalized = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        ValidateHostMatchesTlsConfiguration(uri.Host, tlsServerName, tlsSans);
        return normalized;
    }

    private static void ValidateHostMatchesTlsConfiguration(
        string host,
        string? tlsServerName,
        string? tlsSans)
    {
        var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(tlsServerName))
        {
            expectedNames.Add(tlsServerName.Trim());
        }

        foreach (var san in tlsSans?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     ?? Array.Empty<string>())
        {
            if (san.StartsWith("DNS:", StringComparison.OrdinalIgnoreCase))
            {
                expectedNames.Add(san[4..]);
            }
            else if (san.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            {
                expectedNames.Add(san[3..]);
            }
        }

        if (expectedNames.Count == 0)
        {
            throw new InvalidOperationException(
                "FLOWSTOCK_TLS_SERVER_NAME or FLOWSTOCK_TLS_SANS must be set for FlowStock discovery.");
        }

        if (!expectedNames.Contains(host))
        {
            throw new InvalidOperationException(
                "FLOWSTOCK_PUBLIC_BASE_URL host must match FLOWSTOCK_TLS_SERVER_NAME or FLOWSTOCK_TLS_SANS.");
        }
    }

    private void EnsureUdpResponseFits()
    {
        if (InstanceName.Length > MaxInstanceNameLength)
        {
            throw new InvalidOperationException("FLOWSTOCK_INSTANCE_NAME is too long for UDP discovery.");
        }

        var sample = new FlowStockDiscoveryUdpResponse(
            Product,
            ProtocolVersion,
            "0123456789abcdef0123456789abcdef",
            InstanceName,
            CanonicalHttpsBaseUrl,
            ApplicationVersion);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            sample,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        if (bytes.Length > MaxUdpPacketBytes)
        {
            throw new InvalidOperationException("FlowStock discovery UDP response must be at most 1024 bytes.");
        }
    }
}

public sealed record FlowStockDiscoveryResponse(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("discovery_protocol_version")] int DiscoveryProtocolVersion,
    [property: JsonPropertyName("instance_name")] string InstanceName,
    [property: JsonPropertyName("canonical_https_base_url")] string CanonicalHttpsBaseUrl,
    [property: JsonPropertyName("application_version")] string ApplicationVersion);
