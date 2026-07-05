using System.Net;

namespace FlowStock.DiscoveryRelay;

public sealed record DiscoveryRelayOptions(
    IPEndPoint PublicEndpoint,
    IPEndPoint BackendEndpoint,
    TimeSpan BackendTimeout,
    int MaxInFlight)
{
    public static DiscoveryRelayOptions FromEnvironment() =>
        new(
            new IPEndPoint(IPAddress.Any, DiscoveryRelayConstants.PublicUdpPort),
            new IPEndPoint(IPAddress.Loopback, ReadIntEnv(
                "FLOWSTOCK_DISCOVERY_BACKEND_PORT",
                DiscoveryRelayConstants.DefaultBackendPort,
                1,
                65535,
                disallowed: DiscoveryRelayConstants.PublicUdpPort)),
            TimeSpan.FromMilliseconds(ReadIntEnv(
                "FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS",
                DiscoveryRelayConstants.DefaultTimeoutMs,
                100,
                10_000)),
            ReadIntEnv(
                "FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT",
                DiscoveryRelayConstants.DefaultMaxInFlight,
                1,
                512));

    private static int ReadIntEnv(string name, int defaultValue, int min, int max, int? disallowed = null)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw.Trim(), out var value) || value < min || value > max || value == disallowed)
        {
            var disallowedText = disallowed is int forbidden ? $" and must not be {forbidden}" : "";
            throw new InvalidOperationException($"{name} must be an integer in range {min}..{max}{disallowedText}.");
        }

        return value;
    }
}
